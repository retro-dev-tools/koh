use std::path::PathBuf;
#[cfg(windows)]
use std::{
    io::{Read, Write},
    path::Path,
};

pub fn endpoint(name: &str) -> PathBuf {
    #[cfg(unix)]
    {
        std::env::temp_dir().join(format!("CoreFxPipe_{name}"))
    }
    #[cfg(windows)]
    {
        PathBuf::from(format!("\\\\.\\pipe\\{name}"))
    }
}

#[cfg(unix)]
pub type DapStream = std::os::unix::net::UnixStream;

#[cfg(windows)]
pub struct DapStream {
    file: std::fs::File,
    timeout: std::cell::Cell<std::time::Duration>,
}
#[cfg(windows)]
impl DapStream {
    pub fn connect(path: &Path) -> std::io::Result<Self> {
        Ok(Self {
            file: std::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(path)?,
            timeout: std::cell::Cell::new(std::time::Duration::from_secs(15)),
        })
    }
    pub fn set_read_timeout(&self, timeout: Option<std::time::Duration>) -> std::io::Result<()> {
        self.timeout
            .set(timeout.unwrap_or(std::time::Duration::from_secs(15)));
        Ok(())
    }
}
#[cfg(windows)]
impl std::io::Read for DapStream {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        use std::{ffi::c_void, os::windows::io::AsRawHandle};
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
        let deadline = std::time::Instant::now() + self.timeout.get();
        loop {
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
                let error = std::io::Error::last_os_error();
                return if error.raw_os_error() == Some(109) {
                    Ok(0)
                } else {
                    Err(error)
                };
            }
            if available > 0 {
                return self.file.read(buffer);
            }
            if std::time::Instant::now() >= deadline {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::TimedOut,
                    "DAP response timeout",
                ));
            }
            std::thread::sleep(std::time::Duration::from_millis(1));
        }
    }
}
#[cfg(windows)]
impl std::io::Write for DapStream {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        self.file.write(bytes)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.file.flush()
    }
}
