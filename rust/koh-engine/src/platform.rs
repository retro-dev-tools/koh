use std::sync::atomic::{AtomicBool, Ordering};

static INTERRUPTED: AtomicBool = AtomicBool::new(false);
pub fn interrupted() -> bool {
    INTERRUPTED.load(Ordering::Relaxed)
}

pub struct Runtime;
impl Runtime {
    pub fn new() -> Self {
        #[cfg(unix)]
        unsafe {
            unsafe extern "C" {
                fn signal(signal: i32, handler: usize) -> usize;
            }
            extern "C" fn on_interrupt(_: i32) {
                INTERRUPTED.store(true, Ordering::Relaxed);
            }
            signal(2, on_interrupt as *const () as usize);
        }
        #[cfg(windows)]
        unsafe {
            unsafe extern "system" {
                fn SetConsoleCtrlHandler(
                    handler: unsafe extern "system" fn(u32) -> i32,
                    add: i32,
                ) -> i32;
            }
            #[link(name = "winmm")]
            unsafe extern "system" {
                fn timeBeginPeriod(period: u32) -> u32;
            }
            unsafe extern "system" fn on_interrupt(kind: u32) -> i32 {
                if kind <= 1 {
                    INTERRUPTED.store(true, Ordering::Relaxed);
                    1
                } else {
                    0
                }
            }
            SetConsoleCtrlHandler(on_interrupt, 1);
            timeBeginPeriod(1);
        }
        Self
    }
}
impl Drop for Runtime {
    fn drop(&mut self) {
        #[cfg(windows)]
        unsafe {
            #[link(name = "winmm")]
            unsafe extern "system" {
                fn timeEndPeriod(period: u32) -> u32;
            }
            timeEndPeriod(1);
        }
    }
}
