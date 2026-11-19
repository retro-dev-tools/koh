use libloading::Library;
use std::{
    collections::VecDeque,
    ffi::{c_char, c_void},
};

type Device = *mut c_void;
type Unary = unsafe extern "C" fn(u32);
type Objects = unsafe extern "C" fn(i32, *mut u32);
type Delete = unsafe extern "C" fn(i32, *const u32);
type Queue = unsafe extern "C" fn(u32, i32, *const u32);
type Unqueue = unsafe extern "C" fn(u32, i32, *mut u32);

pub struct Audio {
    _library: Library,
    device: Device,
    context: Device,
    source: u32,
    buffers: [u32; 8],
    free: VecDeque<u32>,
    staging: [i16; 512],
    fill: usize,
    warmed: bool,
    pub underruns: u64,
    pub pushes: u64,
    pub samples: u64,
    close: unsafe extern "C" fn(Device) -> u8,
    destroy: unsafe extern "C" fn(Device),
    current: unsafe extern "C" fn(Device) -> u8,
    delete_sources: Delete,
    delete_buffers: Delete,
    stop: Unary,
    play: Unary,
    rewind: Unary,
    get: unsafe extern "C" fn(u32, i32, *mut i32),
    data: unsafe extern "C" fn(u32, i32, *const c_void, i32, i32),
    queue: Queue,
    unqueue: Unqueue,
}
impl Audio {
    pub fn new() -> Result<Self, String> {
        let names = if cfg!(target_os = "windows") {
            &["soft_oal.dll", "OpenAL32.dll"][..]
        } else if cfg!(target_os = "macos") {
            &[
                "libopenal.dylib",
                "/System/Library/Frameworks/OpenAL.framework/OpenAL",
            ][..]
        } else {
            &["libopenal.so.1", "libopenal.so"][..]
        };
        let mut library = None;
        let directory = std::env::current_exe()
            .ok()
            .and_then(|p| p.parent().map(std::path::Path::to_path_buf));
        for name in names {
            let sibling = directory.as_ref().map(|p| p.join(name));
            let found = sibling
                .as_ref()
                .and_then(|p| unsafe { Library::new(p) }.ok())
                .or_else(|| unsafe { Library::new(name) }.ok());
            if let Some(lib) = found {
                library = Some(lib);
                break;
            }
        }
        let library = library.ok_or("OpenAL library unavailable")?;
        unsafe {
            macro_rules! load {
                ($name:literal,$ty:ty) => {
                    *library
                        .get::<$ty>(concat!($name, "\0").as_bytes())
                        .map_err(|e| e.to_string())?
                };
            }
            let open = load!(
                "alcOpenDevice",
                unsafe extern "C" fn(*const c_char) -> Device
            );
            let create = load!(
                "alcCreateContext",
                unsafe extern "C" fn(Device, *const i32) -> Device
            );
            let current = load!("alcMakeContextCurrent", unsafe extern "C" fn(Device) -> u8);
            let close = load!("alcCloseDevice", unsafe extern "C" fn(Device) -> u8);
            let destroy = load!("alcDestroyContext", unsafe extern "C" fn(Device));
            let gen_sources = load!("alGenSources", Objects);
            let gen_buffers = load!("alGenBuffers", Objects);
            let source_i = load!("alSourcei", unsafe extern "C" fn(u32, i32, i32));
            let source_f = load!("alSourcef", unsafe extern "C" fn(u32, i32, f32));
            let delete_sources = load!("alDeleteSources", Delete);
            let delete_buffers = load!("alDeleteBuffers", Delete);
            let stop = load!("alSourceStop", Unary);
            let play = load!("alSourcePlay", Unary);
            let rewind = load!("alSourceRewind", Unary);
            let get = load!("alGetSourcei", unsafe extern "C" fn(u32, i32, *mut i32));
            let data = load!(
                "alBufferData",
                unsafe extern "C" fn(u32, i32, *const c_void, i32, i32)
            );
            let queue = load!("alSourceQueueBuffers", Queue);
            let unqueue = load!("alSourceUnqueueBuffers", Unqueue);
            let device = open(c"".as_ptr());
            if device.is_null() {
                return Err("alcOpenDevice returned null — no audio output available".into());
            }
            let context = create(device, std::ptr::null());
            if context.is_null() {
                close(device);
                return Err("alcCreateContext failed".into());
            }
            if current(context) == 0 {
                destroy(context);
                close(device);
                return Err("alcMakeContextCurrent failed".into());
            }
            let mut source = 0;
            gen_sources(1, &mut source);
            source_i(source, 0x1007, 0);
            source_f(source, 0x100a, 1.0);
            let mut buffers = [0; 8];
            gen_buffers(8, buffers.as_mut_ptr());
            Ok(Self {
                _library: library,
                device,
                context,
                source,
                buffers,
                free: buffers.into(),
                staging: [0; 512],
                fill: 0,
                warmed: false,
                underruns: 0,
                pushes: 0,
                samples: 0,
                close,
                destroy,
                current,
                delete_sources,
                delete_buffers,
                stop,
                play,
                rewind,
                get,
                data,
                queue,
                unqueue,
            })
        }
    }
    fn property(&self, param: i32) -> i32 {
        let mut v = 0;
        unsafe { (self.get)(self.source, param, &mut v) };
        v
    }
    fn recycle(&mut self) {
        for _ in 0..self.property(0x1016) {
            let mut buffer = 0;
            unsafe { (self.unqueue)(self.source, 1, &mut buffer) };
            self.free.push_back(buffer);
        }
    }
    pub fn push(&mut self, samples: &[i16]) -> i32 {
        self.pushes += 1;
        self.samples += samples.len() as u64;
        self.recycle();
        let mut consumed = 0;
        while !self.free.is_empty() {
            let count = (512 - self.fill).min(samples.len() - consumed);
            if count == 0 {
                break;
            }
            self.staging[self.fill..self.fill + count]
                .copy_from_slice(&samples[consumed..consumed + count]);
            self.fill += count;
            consumed += count;
            if self.fill < 512 {
                break;
            }
            let buffer = self.free.pop_front().unwrap();
            unsafe {
                (self.data)(buffer, 0x1101, self.staging.as_ptr().cast(), 1024, 44100);
                (self.queue)(self.source, 1, &buffer)
            }
            self.fill = 0;
        }
        let state = self.property(0x1010);
        if state != 0x1012 {
            let queued = self.property(0x1015) * 512;
            if self.warmed {
                if queued > 0 {
                    if state == 0x1014 {
                        self.underruns += 1;
                    }
                    unsafe { (self.play)(self.source) };
                }
            } else if queued >= 7 * 512 {
                self.warmed = true;
                unsafe { (self.play)(self.source) };
            }
        }
        self.buffered()
    }
    pub fn buffered(&self) -> i32 {
        (self.property(0x1015) * 512 - self.property(0x1025)).max(0)
    }
    pub fn reset(&mut self) {
        unsafe { (self.stop)(self.source) };
        for _ in 0..self.property(0x1015) {
            let mut buffer = 0;
            unsafe { (self.unqueue)(self.source, 1, &mut buffer) }
            if buffer != 0 {
                self.free.push_back(buffer);
            }
        }
        unsafe { (self.rewind)(self.source) };
        self.fill = 0;
        self.warmed = false;
    }
}
impl Drop for Audio {
    fn drop(&mut self) {
        self.reset();
        unsafe {
            (self.delete_sources)(1, &self.source);
            (self.delete_buffers)(8, self.buffers.as_ptr());
            (self.current)(std::ptr::null_mut());
            (self.destroy)(self.context);
            (self.close)(self.device);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "requires ALSOFT_DRIVERS=null and an OpenAL library"]
    fn openal_null_backend_warmup_reset_and_restart() {
        assert_eq!(std::env::var("ALSOFT_DRIVERS").as_deref(), Ok("null"));
        let mut sink = Audio::new().unwrap();
        for _ in 0..3 {
            sink.reset();
            assert!(!sink.warmed);
            assert_eq!(sink.buffered(), 0);
            for frame in 0..9 {
                let buffered = sink.push(&[0; 738]);
                if frame < 4 {
                    assert!(!sink.warmed);
                } else {
                    assert!(sink.warmed);
                    assert_eq!(sink.property(0x1010), 0x1012);
                    assert!(buffered > 0);
                }
            }
            assert!(sink.buffered() > 738 * 2);
        }
        assert_eq!(sink.underruns, 0);
    }
}
