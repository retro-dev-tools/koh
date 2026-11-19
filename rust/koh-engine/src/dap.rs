use crate::{Message, protocol};
use std::{
    io::{self, Write},
    sync::{Arc, atomic::AtomicBool, mpsc::SyncSender},
    thread,
};
#[cfg(unix)]
use std::{sync::atomic::Ordering, time::Duration};

pub struct Listener {
    path: Option<std::path::PathBuf>,
}

#[cfg(windows)]
struct PipeReader {
    file: std::fs::File,
    cancel: Arc<AtomicBool>,
}

#[cfg(windows)]
impl io::Read for PipeReader {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        use std::{
            ffi::c_void, os::windows::io::AsRawHandle, sync::atomic::Ordering, time::Duration,
        };
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn PeekNamedPipe(
                pipe: *mut c_void,
                buffer: *mut c_void,
                size: u32,
                read: *mut u32,
                available: *mut u32,
                left: *mut u32,
            ) -> i32;
        }
        if buffer.is_empty() {
            return Ok(0);
        }
        loop {
            if self.cancel.load(Ordering::Relaxed) {
                return Ok(0);
            }
            let mut available = 0;
            if unsafe {
                PeekNamedPipe(
                    self.file.as_raw_handle(),
                    std::ptr::null_mut(),
                    0,
                    std::ptr::null_mut(),
                    &mut available,
                    std::ptr::null_mut(),
                )
            } == 0
            {
                let error = io::Error::last_os_error();
                return if matches!(error.raw_os_error(), Some(109 | 232)) {
                    Ok(0)
                } else {
                    Err(error)
                };
            }
            if available > 0 {
                let count = buffer.len().min(available as usize);
                return io::Read::read(&mut self.file, &mut buffer[..count]);
            }
            thread::sleep(Duration::from_millis(1));
        }
    }
}
impl Drop for Listener {
    fn drop(&mut self) {
        if let Some(path) = &self.path {
            let _ = std::fs::remove_file(path);
        }
    }
}

pub fn listen(
    name: String,
    sender: SyncSender<Message>,
    cancel: Arc<AtomicBool>,
) -> io::Result<Listener> {
    if name.is_empty()
        || name.contains('\\')
        || (name.contains('/') && (!cfg!(unix) || !std::path::Path::new(&name).is_absolute()))
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Invalid pipe name",
        ));
    }
    #[cfg(unix)]
    {
        use std::os::unix::net::UnixListener;
        let path = if std::path::Path::new(&name).is_absolute() {
            std::path::PathBuf::from(&name)
        } else {
            std::env::temp_dir().join(format!("CoreFxPipe_{name}"))
        };
        let listener = UnixListener::bind(&path)?;
        listener.set_nonblocking(true)?;
        eprintln!("[koh-dap] listening on \\\\.\\pipe\\{name}");
        let cleanup = path.clone();
        thread::spawn(move || {
            let result = (|| -> io::Result<()> {
                let (mut stream, _) = loop {
                    if cancel.load(Ordering::Relaxed) {
                        return Ok(());
                    }
                    match listener.accept() {
                        Ok(pair) => break pair,
                        Err(e) if e.kind() == io::ErrorKind::WouldBlock => {
                            thread::sleep(Duration::from_millis(5))
                        }
                        Err(e) => return Err(e),
                    }
                };
                stream.set_nonblocking(false)?;
                eprintln!("[koh-dap] client connected");
                let writer = stream.try_clone()?;
                sender
                    .send(Message::DapConnected(Box::new(writer)))
                    .map_err(|e| io::Error::other(e.to_string()))?;
                while let Some(value) = protocol::read_frame(&mut stream)? {
                    if sender.send(Message::Dap(value)).is_err() {
                        break;
                    }
                }
                Ok(())
            })();
            if let Err(e) = result {
                eprintln!("[koh-dap] server crashed: {e}");
            }
            let _ = std::fs::remove_file(cleanup);
            let _ = sender.send(Message::DapClosed);
        });
        Ok(Listener { path: Some(path) })
    }
    #[cfg(windows)]
    {
        use std::ffi::c_void;
        use std::{
            fs::File,
            os::windows::{ffi::OsStrExt, io::FromRawHandle},
        };
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn CreateNamedPipeW(
                name: *const u16,
                mode: u32,
                pipe_mode: u32,
                instances: u32,
                out_size: u32,
                in_size: u32,
                timeout: u32,
                security: *const c_void,
            ) -> *mut c_void;
            fn ConnectNamedPipe(pipe: *mut c_void, overlapped: *mut c_void) -> i32;
        }
        let path: Vec<u16> = std::ffi::OsStr::new(&format!("\\\\.\\pipe\\{name}"))
            .encode_wide()
            .chain(Some(0))
            .collect();
        let handle =
            unsafe { CreateNamedPipeW(path.as_ptr(), 3, 0, 1, 65536, 65536, 0, std::ptr::null()) };
        if handle as isize == -1 {
            return Err(io::Error::last_os_error());
        }
        let stream = unsafe { File::from_raw_handle(handle) };
        eprintln!("[koh-dap] listening on \\\\.\\pipe\\{name}");
        thread::spawn(move || {
            let result = (|| -> io::Result<()> {
                use std::os::windows::io::AsRawHandle;
                if unsafe { ConnectNamedPipe(stream.as_raw_handle(), std::ptr::null_mut()) } == 0
                    && io::Error::last_os_error().raw_os_error() != Some(535)
                {
                    return Err(io::Error::last_os_error());
                }
                sender
                    .send(Message::DapConnected(Box::new(stream.try_clone()?)))
                    .map_err(|e| io::Error::other(e.to_string()))?;
                let mut reader = PipeReader {
                    file: stream,
                    cancel,
                };
                while let Some(value) = protocol::read_frame(&mut reader)? {
                    if sender.send(Message::Dap(value)).is_err() {
                        break;
                    }
                }
                Ok(())
            })();
            if let Err(e) = result {
                eprintln!("[koh-dap] server crashed: {e}");
            }
            let _ = sender.send(Message::DapClosed);
        });
        Ok(Listener { path: None })
    }
}

pub fn send(writer: &mut Option<Box<dyn Write + Send>>, value: &serde_json::Value) {
    if writer
        .as_mut()
        .is_some_and(|output| protocol::write_frame(output, value).is_err())
    {
        *writer = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Closed;
    impl Write for Closed {
        fn write(&mut self, _: &[u8]) -> io::Result<usize> {
            Err(io::Error::new(io::ErrorKind::BrokenPipe, "closed DAP peer"))
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }
    #[test]
    fn closed_dap_writer_does_not_turn_disconnect_into_failure() {
        let mut writer: Option<Box<dyn Write + Send>> = Some(Box::new(Closed));
        send(&mut writer, &serde_json::json!({"event":"stopped"}));
        assert!(writer.is_none());
    }
}
