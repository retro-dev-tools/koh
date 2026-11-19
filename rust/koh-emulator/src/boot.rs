#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn validates_family_shape_and_stable_fingerprint() {
        assert!(BootRom::new(vec![0; 255]).is_err());
        let dmg = BootRom::new(vec![0; 256]).unwrap();
        assert_eq!(dmg.family, HardwareMode::Dmg);
        assert!(dmg.contains(255));
        assert!(!dmg.contains(256));
        assert_ne!(dmg.fingerprint, 0);
        let cgb = BootRom::new(vec![0; 2304]).unwrap();
        assert!(cgb.contains(0));
        assert!(!cgb.contains(0x100));
        assert!(cgb.contains(0x200));
        assert!(cgb.contains(0x8ff));
        assert!(!cgb.contains(0x900));
    }
}
use crate::HardwareMode;

pub const DMG_BOOT: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/dmg_boot.bin"));
pub const CGB_BOOT: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/cgb_boot.bin"));

#[derive(Clone)]
pub struct BootRom {
    pub bytes: Vec<u8>,
    pub family: HardwareMode,
    pub fingerprint: u64,
}
impl BootRom {
    pub fn new(bytes: Vec<u8>) -> Result<Self, String> {
        let family = match bytes.len() {
            256 => HardwareMode::Dmg,
            2304 => HardwareMode::Cgb,
            n => {
                return Err(format!(
                    "Boot ROM is {n} bytes; expected 256 (DMG) or 2304 (CGB) bytes"
                ));
            }
        };
        let mut fingerprint = 14695981039346656037u64;
        for &b in &bytes {
            fingerprint = (fingerprint ^ b as u64).wrapping_mul(1099511628211);
        }
        Ok(Self {
            bytes,
            family,
            fingerprint: fingerprint.max(1),
        })
    }
    pub fn contains(&self, address: u16) -> bool {
        address < 0x100 || (self.family == HardwareMode::Cgb && (0x200..0x900).contains(&address))
    }
}
