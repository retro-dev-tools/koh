use super::Error;
use clrmeta::{MetadataRoot, reader::Reader};
use object::{FileKind, pe};
use sha2::{Digest, Sha256, Sha384, Sha512};

struct Identity {
    id: [u8; 20],
    checksums: Vec<(usize, Vec<u8>)>,
}

fn records(bytes: &[u8], image: &[u8]) -> Result<Identity, Error> {
    if bytes.len() % 28 != 0 || bytes.len() / 28 > 1024 {
        return Err("Invalid PE debug directory size".into());
    }
    let mut identity = None;
    let mut checksums = Vec::new();
    for entry in bytes.chunks_exact(28) {
        let mut reader = Reader::new(entry);
        reader.read_u32()?;
        let stamp = reader.read_u32()?;
        let major = reader.read_u16()?;
        let minor = reader.read_u16()?;
        let kind = reader.read_u32()?;
        let size = reader.read_u32()? as usize;
        reader.read_u32()?;
        let offset = reader.read_u32()? as usize;
        if kind != 19 && !(kind == 2 && minor == 0x504d) {
            continue;
        }
        let end = offset
            .checked_add(size)
            .ok_or("PE debug payload range overflow")?;
        let data = image.get(offset..end).ok_or("Truncated PE debug payload")?;
        if kind == 2 {
            if identity.is_some() {
                return Err("Multiple Portable CodeView identities are unsupported".into());
            }
            let mut reader = Reader::new(data);
            if reader.read_bytes(4)? != b"RSDS" {
                return Err("Invalid Portable CodeView signature".into());
            }
            let mut id = [0; 20];
            id[..16].copy_from_slice(reader.read_bytes(16)?);
            id[16..].copy_from_slice(&stamp.to_le_bytes());
            if reader.read_u32()? != 1 || reader.remaining() > 65_536 {
                return Err("Invalid Portable CodeView age or path length".into());
            }
            reader.read_null_str()?;
            identity = Some(id);
        } else {
            if major != 1 || minor != 0 {
                return Err("Unsupported PDB checksum record version".into());
            }
            if checksums.len() >= 32 || data.len() > 128 {
                return Err("PDB checksum record count or size exceeds limit".into());
            }
            let mut reader = Reader::new(data);
            let algorithm = match reader.read_null_str()? {
                "SHA256" => 0,
                "SHA384" => 1,
                "SHA512" => 2,
                _ => return Err("Unsupported PDB checksum algorithm".into()),
            };
            let expected = reader.read_bytes([32, 48, 64][algorithm])?;
            if reader.remaining() != 0 {
                return Err("Invalid PDB checksum size".into());
            }
            checksums.push((algorithm, expected.to_vec()));
        }
    }
    Ok(Identity {
        id: identity.ok_or("Assembly has no Portable CodeView identity")?,
        checksums,
    })
}

fn from_image(image: &[u8]) -> Result<Identity, Error> {
    let (sections, directory) = match FileKind::parse(image)? {
        FileKind::Pe32 => {
            let file = object::read::pe::PeFile32::parse(image)?;
            (
                file.section_table(),
                file.data_directory(pe::IMAGE_DIRECTORY_ENTRY_DEBUG),
            )
        }
        FileKind::Pe64 => {
            let file = object::read::pe::PeFile64::parse(image)?;
            (
                file.section_table(),
                file.data_directory(pe::IMAGE_DIRECTORY_ENTRY_DEBUG),
            )
        }
        _ => return Err("Expected a PE image for PDB matching".into()),
    };
    let data = directory
        .ok_or("Assembly has no PE debug directory")?
        .data(image, &sections)?;
    records(data, image)
}

fn hash<D: Digest>(bytes: &[u8], offset: usize) -> Vec<u8> {
    let mut hash = D::new();
    hash.update(&bytes[..offset]);
    hash.update([0; 20]);
    hash.update(&bytes[offset + 20..]);
    hash.finalize().to_vec()
}

pub(super) fn validate(image: &[u8], bytes: &[u8], id: &[u8; 20]) -> Result<(), String> {
    (|| -> Result<(), Error> {
        let identity = from_image(image)?;
        if identity.id != *id {
            return Err("Portable PDB identity does not match assembly GUID and timestamp".into());
        }
        if !identity.checksums.is_empty() {
            let root = MetadataRoot::parse(bytes)?;
            let offset = root
                .find_stream("#Pdb")
                .ok_or("Missing #Pdb stream")?
                .offset as usize;
            if bytes
                .get(offset..offset.checked_add(20).ok_or("PDB ID range overflow")?)
                .is_none()
            {
                return Err("Truncated PDB ID".into());
            }
            let mut hashes: [Option<Vec<u8>>; 3] = [None, None, None];
            let mut matches = false;
            for (algorithm, expected) in identity.checksums {
                let actual = hashes[algorithm].get_or_insert_with(|| match algorithm {
                    0 => hash::<Sha256>(bytes, offset),
                    1 => hash::<Sha384>(bytes, offset),
                    _ => hash::<Sha512>(bytes, offset),
                });
                matches |= *actual == expected;
            }
            if !matches {
                return Err("Portable PDB checksum does not match assembly".into());
            }
        }
        Ok(())
    })()
    .map_err(|error| format!("PDB matching: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use rstest::rstest;

    fn entry(kind: u32, major: u16, minor: u16, stamp: u32, offset: u32, size: u32) -> Vec<u8> {
        let mut bytes = vec![0; 4];
        bytes.extend(stamp.to_le_bytes());
        bytes.extend(major.to_le_bytes());
        bytes.extend(minor.to_le_bytes());
        bytes.extend(kind.to_le_bytes());
        bytes.extend(size.to_le_bytes());
        bytes.extend(0u32.to_le_bytes());
        bytes.extend(offset.to_le_bytes());
        bytes
    }

    fn codeview(age: u32) -> Vec<u8> {
        let mut bytes = b"RSDS".to_vec();
        bytes.extend([0x42; 16]);
        bytes.extend(age.to_le_bytes());
        bytes.extend(b"ignored/path.pdb\0");
        bytes
    }

    #[rstest]
    #[case("SHA256", 32, 0)]
    #[case("SHA384", 48, 1)]
    #[case("SHA512", 64, 2)]
    fn portable_codeview_uses_guid_and_directory_stamp(
        #[case] name: &str,
        #[case] size: usize,
        #[case] algorithm: usize,
    ) {
        let mut image = codeview(1);
        let mut directory = entry(2, 0x100, 0x504d, 0x12345678, 0, image.len() as u32);
        let offset = image.len();
        image.extend(name.as_bytes());
        image.push(0);
        image.extend(vec![0x55; size]);
        directory.extend(entry(
            19,
            1,
            0,
            0,
            offset as u32,
            (image.len() - offset) as u32,
        ));
        let identity = records(&directory, &image).unwrap();
        assert_eq!(&identity.id[..16], &[0x42; 16]);
        assert_eq!(&identity.id[16..], &[0x78, 0x56, 0x34, 0x12]);
        assert_eq!(identity.checksums, [(algorithm, vec![0x55; size])]);
    }

    #[test]
    fn unsupported_checksum_version_diagnoses() {
        let mut image = codeview(1);
        let mut directory = entry(2, 0x100, 0x504d, 1, 0, image.len() as u32);
        let offset = image.len();
        image.extend(b"SHA256\0");
        image.extend([0; 32]);
        directory.extend(entry(19, 2, 0, 0, offset as u32, 39));
        let error = records(&directory, &image)
            .err()
            .expect("unsupported version");
        assert!(error.to_string().contains("version"), "{error}");
    }

    #[rstest]
    #[case(0)]
    #[case(2)]
    #[case(u32::MAX)]
    fn portable_age_must_be_one(#[case] age: u32) {
        let image = codeview(age);
        let directory = entry(2, 0x100, 0x504d, 1, 0, image.len() as u32);
        assert!(
            records(&directory, &image)
                .err()
                .unwrap()
                .to_string()
                .contains("age")
        );
    }

    #[test]
    fn duplicate_or_truncated_records_diagnose() {
        let image = codeview(1);
        let directory = entry(2, 0x100, 0x504d, 1, 0, image.len() as u32);
        for length in 0..image.len() {
            assert!(records(&directory, &image[..length]).is_err());
            let prefix = entry(2, 0x100, 0x504d, 1, 0, length as u32);
            assert!(records(&prefix, &image).is_err());
        }
        assert!(records(&directory, &image).unwrap().checksums.is_empty());
        let duplicate = [directory.as_slice(), directory.as_slice()].concat();
        assert!(
            records(&duplicate, &image)
                .err()
                .unwrap()
                .to_string()
                .contains("Multiple")
        );
        assert!(records(&directory[..27], &image).is_err());
        assert!(records(&vec![0; 28 * 1025], &image).is_err());
    }

    #[test]
    fn checksum_zeroes_only_the_twenty_byte_id() {
        let mut bytes = vec![1, 2, 3];
        bytes.extend([0xff; 20]);
        bytes.extend([4, 5]);
        let hex = |bytes: Vec<u8>| bytes.iter().map(|b| format!("{b:02x}")).collect::<String>();
        assert_eq!(
            hex(hash::<Sha256>(&bytes, 3)),
            "24bcc32a1c88cfc1a55d6fe211f0e127ad85228f00f8d22739d8495e859d52bc"
        );
        assert_eq!(
            hex(hash::<Sha384>(&bytes, 3)),
            "171a8f19701116828786f370b1aaecd46c3c4cc5e7c23073893e1dd0d3be0ce1c7ba108184bde1836a4b07d13241fc1a"
        );
        assert_eq!(
            hex(hash::<Sha512>(&bytes, 3)),
            "bdf682c4da6b92d3737dad9ffa68fe4063f96738881f27508d8c7c7a629b8fe0ab66a0858efdd062b0d0ed7462c2281f1a8b18a3b6ea2c21de8504d279381bb7"
        );
    }
}
