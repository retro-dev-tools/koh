use super::{Host, execute_mode, host};
use koh_compile::{compile, resolve::Resolver};
use rstest::rstest;
use std::{collections::BTreeMap, fmt::Write, path::Path};

const MUL32: &[(i128, i128)] = &[
    (0, 0),
    (0, 12345),
    (12345, 0),
    (1, 1),
    (300000, 5),
    (5, 300000),
    (-5, 300000),
    (300000, -5),
    (-70000, -30000),
    (i32::MAX as i128, 2),
    (i32::MIN as i128, 1),
    (i32::MIN as i128, -1),
    (65536, 65536),
    (16777216, 3),
    (3, 16777216),
    (-1, -1),
    (-1, 1),
];
const DIV32: &[(i128, i128)] = &[
    (0, 5),
    (300000, 5),
    (300000, 7),
    (-300000, 5),
    (300000, -5),
    (-300000, -5),
    (5, 300000),
    (16777216, 3),
    (3, 16777216),
    (i32::MAX as i128, 7),
    (i32::MIN as i128, 7),
    (i32::MIN as i128, 2),
    (-1, 1),
    (1, -1),
    (-1, -1),
    (70000, 70000),
    (i32::MAX as i128, 1),
];
const POW2: &[(i128, i128)] = &[
    (0, 128),
    (1, 128),
    (127, 128),
    (128, 128),
    (129, 128),
    (255, 128),
    (256, 128),
    (-1, 128),
    (-127, 128),
    (-128, 128),
    (-129, 128),
    (-256, 128),
    (i32::MAX as i128, 128),
    (i32::MIN as i128, 128),
    (-1, 2),
    (1, 2),
    (-3, 2),
    (3, 2),
    (i32::MIN as i128, 2),
];
const MUL64: &[(i128, i128)] = &[
    (0, 12345),
    (1, 1),
    (300000, 5),
    (5_000_000_000, 3),
    (3, 5_000_000_000),
    (-5_000_000_000, 3),
    (5_000_000_000, -3),
    (i64::MAX as i128, 2),
    (i64::MIN as i128, 1),
    (i64::MIN as i128, -1),
    (-1, -1),
];
const DIV64: &[(i128, i128)] = &[
    (0, 5),
    (5_000_000_000, 3),
    (-5_000_000_000, 3),
    (5_000_000_000, -3),
    (3, 5_000_000_000),
    (i64::MAX as i128, 7),
    (i64::MIN as i128, 7),
    (i64::MIN as i128, 2),
    (-1, 1),
    (1, -1),
    (-1, -1),
];
const SHIFT32: &[i128] = &[
    0,
    1,
    -1,
    5,
    -5,
    1000,
    -1000,
    i32::MAX as i128,
    i32::MIN as i128,
    -123456,
    123456,
];
const SHIFT64: &[i128] = &[
    0,
    1,
    -1,
    1000,
    -1000,
    i64::MAX as i128,
    i64::MIN as i128,
    -123456789012345,
];
const COUNTS32: &[i128] = &[
    0,
    1,
    7,
    8,
    9,
    15,
    16,
    17,
    23,
    24,
    25,
    31,
    -1,
    32,
    33,
    63,
    64,
    65,
    255,
    i32::MIN as i128,
    i32::MAX as i128,
];
const COUNTS64: &[i128] = &[
    0,
    1,
    8,
    20,
    24,
    40,
    63,
    -1,
    64,
    65,
    127,
    128,
    255,
    i32::MIN as i128,
    i32::MAX as i128,
];

#[derive(Clone, Copy)]
enum Operation {
    Mul,
    Div,
    Rem,
    Pow2,
    Shl,
    Shr,
    Logical,
}

struct Group {
    name: String,
    ty: &'static str,
    bytes: usize,
    op: Operation,
    left: Vec<i128>,
    right: Vec<i128>,
    constant: bool,
    fault: Option<&'static str>,
}

impl Group {
    fn pairs(
        name: &str,
        ty: &'static str,
        bytes: usize,
        op: Operation,
        pairs: &[(i128, i128)],
    ) -> Self {
        let (left, right) = pairs.iter().copied().unzip();
        Self {
            name: name.into(),
            ty,
            bytes,
            op,
            left,
            right,
            constant: false,
            fault: None,
        }
    }
    fn shift(&self) -> bool {
        matches!(
            self.op,
            Operation::Shl | Operation::Shr | Operation::Logical
        )
    }
    fn results(&self) -> usize {
        if self.shift() {
            self.left.len() * self.right.len()
        } else {
            self.left.len()
        }
    }
    fn expected(&self) -> Vec<u8> {
        let bits = self.bytes * 8;
        let mask = (1u128 << bits) - 1;
        let mut bytes = Vec::new();
        let pairs: Vec<_> = if self.shift() {
            self.right
                .iter()
                .flat_map(|&r| self.left.iter().map(move |&l| (l, r)))
                .collect()
        } else {
            self.left
                .iter()
                .copied()
                .zip(self.right.iter().copied())
                .collect()
        };
        for (left, right) in pairs {
            let count = (right as u32) & (bits as u32 - 1);
            let result = match self.op {
                Operation::Mul => (left as u128).wrapping_mul(right as u128),
                Operation::Div | Operation::Pow2 => (left / right) as u128,
                Operation::Rem => (left % right) as u128,
                Operation::Shl => (left as u128) << count,
                Operation::Shr => (left >> count) as u128,
                Operation::Logical => ((left as u128) & mask) >> count,
            } & mask;
            bytes.extend_from_slice(&result.to_le_bytes()[..self.bytes]);
        }
        bytes
    }
}

fn groups() -> Vec<Group> {
    let mut groups = vec![
        Group::pairs("Mul32", "int", 4, Operation::Mul, MUL32),
        Group::pairs("Div32", "int", 4, Operation::Div, DIV32),
        Group::pairs("Pow2", "int", 4, Operation::Pow2, POW2),
        Group::pairs("Mul64", "long", 8, Operation::Mul, MUL64),
        Group::pairs("Div64", "long", 8, Operation::Div, DIV64),
    ];
    for (bits, ty, bytes, div, mul) in [(32, "int", 4, DIV32, MUL32), (64, "long", 8, DIV64, MUL64)]
    {
        let minimum = -(1i128 << (bits - 1));
        let maximum = (1i128 << (bits - 1)) - 1;
        let remainder = div.to_vec();
        groups.push(Group::pairs(
            &format!("Rem{bits}"),
            ty,
            bytes,
            Operation::Rem,
            &remainder,
        ));
        let mut multiply = mul.to_vec();
        multiply.extend([
            (minimum, minimum),
            (maximum, maximum),
            (0x01020304, 0x05060708),
        ]);
        groups.push(Group::pairs(
            &format!("ExtraMul{bits}"),
            ty,
            bytes,
            Operation::Mul,
            &multiply,
        ));
        for (name, op, left, right) in [
            ("DivZero", Operation::Div, 1, 0),
            ("RemZero", Operation::Rem, 1, 0),
            ("DivOverflow", Operation::Div, minimum, -1),
            ("RemOverflow", Operation::Rem, minimum, -1),
        ] {
            let mut group = Group::pairs(&format!("{name}{bits}"), ty, bytes, op, &[(left, right)]);
            group.fault = Some(if right == 0 {
                "System.DivideByZeroException"
            } else {
                "System.OverflowException"
            });
            groups.push(group);
        }
        let unsigned = if bits == 32 { "uint" } else { "ulong" };
        let unsigned_max = (1i128 << bits) - 1;
        let cases = [
            (0, 1),
            (unsigned_max, 1),
            (unsigned_max, 2),
            (unsigned_max, 7),
            (unsigned_max, unsigned_max),
            ((1i128 << (bits - 1)), 3),
            (3, unsigned_max),
            (65536, 257),
        ];
        for (name, op) in [("UDiv", Operation::Div), ("URem", Operation::Rem)] {
            groups.push(Group::pairs(
                &format!("{name}{bits}"),
                unsigned,
                bytes,
                op,
                &cases,
            ));
            let mut group = Group::pairs(
                &format!("{name}Zero{bits}"),
                unsigned,
                bytes,
                op,
                &[(unsigned_max, 0)],
            );
            group.fault = Some("System.DivideByZeroException");
            groups.push(group);
        }
    }
    for (bits, ty, bytes, operands, counts) in [
        (32, "int", 4, SHIFT32, COUNTS32),
        (64, "long", 8, SHIFT64, COUNTS64),
    ] {
        for (name, op) in [
            ("Shl", Operation::Shl),
            ("Shr", Operation::Shr),
            ("Logical", Operation::Logical),
        ] {
            for constant in [true, false] {
                groups.push(Group {
                    name: format!("{name}{bits}{}", if constant { "Const" } else { "Dynamic" }),
                    ty,
                    bytes,
                    op,
                    left: operands.to_vec(),
                    right: counts.to_vec(),
                    constant,
                    fault: None,
                });
            }
        }
    }
    groups
}

fn literal(value: i128, ty: &str) -> String {
    format!(
        "{value}{}",
        match ty {
            "long" => "L",
            "uint" => "U",
            "ulong" => "UL",
            _ => "",
        }
    )
}

fn source(groups: &[Group]) -> String {
    let mut arrays = BTreeMap::<(String, Vec<i128>), String>::new();
    let mut fields = String::new();
    let mut methods = String::new();
    for group in groups {
        let mut array = |ty: &str, values: &[i128]| {
            let key = (ty.to_owned(), values.to_vec());
            if let Some(name) = arrays.get(&key) {
                return name.clone();
            }
            let name = format!("V{}", arrays.len());
            writeln!(
                fields,
                "static {ty}[] {name} = {{ {} }};",
                values
                    .iter()
                    .map(|&v| literal(v, ty))
                    .collect::<Vec<_>>()
                    .join(",")
            )
            .unwrap();
            arrays.insert(key, name.clone());
            name
        };
        let left = array(group.ty, &group.left);
        let right = if group.constant {
            None
        } else {
            Some(array(
                if group.shift() { "int" } else { group.ty },
                &group.right,
            ))
        };
        let name = &group.name;
        let ty = group.ty;
        let bytes = group.bytes;
        let op = match group.op {
            Operation::Mul => "*",
            Operation::Div => "/",
            Operation::Rem => "%",
            Operation::Shl => "<<",
            Operation::Shr => ">>",
            Operation::Logical => ">>>",
            Operation::Pow2 => "/",
        };
        if group.constant {
            writeln!(
                methods,
                "static {ty} Value{name}({ty} value,int index) {{switch(index) {{"
            )
            .unwrap();
            for (index, &count) in group.right.iter().enumerate() {
                writeln!(methods, "case {index}: return value {op} {count};").unwrap();
            }
            writeln!(methods, "default: return value;}}}}").unwrap();
        }
        writeln!(
            methods,
            "static void Compute{name}() {{ Results=new byte[{}]; fixed(byte* output=Results) {{",
            group.results() * bytes
        )
        .unwrap();
        if group.shift() {
            writeln!(
                methods,
                "for(int row=0;row<{};row++) for(int column=0;column<{};column++) {{",
                group.right.len(),
                group.left.len()
            )
            .unwrap();
            let value = if group.constant {
                format!("Value{name}({left}[column],row)")
            } else {
                format!("{left}[column] {op} {}[row]", right.as_ref().unwrap())
            };
            writeln!(
                methods,
                "*({ty}*)(output+(row*{}+column)*{bytes})=unchecked({value}); }}",
                group.left.len()
            )
            .unwrap();
        } else {
            let right = right.unwrap();
            let value = if matches!(group.op, Operation::Pow2) {
                format!("{right}[i]==128 ? {left}[i]/128 : {left}[i]/2")
            } else {
                format!("{left}[i] {op} {right}[i]")
            };
            writeln!(
                methods,
                "for(int i=0;i<{};i++) *({ty}*)(output+i*{bytes})=unchecked({value});",
                group.left.len()
            )
            .unwrap();
        }
        writeln!(methods,"}}}} public static int {name}() {{Compute{name}(); return Address();}} public static string Managed{name}() {{Compute{name}();return System.Convert.ToHexString(Results);}}").unwrap();
    }
    format!(
        "public static unsafe class Program {{ {fields} static byte[] Results; static int Address() {{fixed(byte* pointer=Results) return (ushort)pointer;}} {methods} }}"
    )
}

fn images(host: &Host, configuration: &str, source: &str) -> (tempfile::TempDir, [Vec<u8>; 2]) {
    let directory = tempfile::Builder::new()
        .prefix("koh wide arithmetic ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap();
    let project = directory.path().join("WideArithmetic.csproj");
    std::fs::write(&project,"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors><NuGetAudit>true</NuGetAudit><AllowUnsafeBlocks>true</AllowUnsafeBlocks><DebugType>portable</DebugType></PropertyGroup><ItemGroup><ProjectReference Include=\"$(KohRoot)/src/Compiler/Koh.GameBoy/Koh.GameBoy.csproj\" /></ItemGroup></Project>").unwrap();
    std::fs::write(directory.path().join("Program.cs"), source).unwrap();
    let result = super::dotnet()
        .arg("build")
        .arg(&project)
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg(format!("-p:KohRoot={}", root.display()))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}{}",
        String::from_utf8_lossy(&result.stdout),
        String::from_utf8_lossy(&result.stderr)
    );
    let output = directory
        .path()
        .join(format!("bin/{configuration}/net10.0"));
    assert!(output.join("WideArithmetic.pdb").is_file());
    let images = [
        std::fs::read(output.join("WideArithmetic.dll")).unwrap(),
        std::fs::read(output.join("Koh.GameBoy.dll")).unwrap(),
    ];
    (directory, images)
}

#[rstest::fixture]
#[once]
fn managed() -> Host {
    super::support::build_fixture_host("ManagedHost")
}

#[rstest]
#[case(false)]
#[case(true)]
fn roslyn_wide_arithmetic_vectors_match_ordered_rom_bytes(
    host: &Host,
    managed: &Host,
    #[case] color: bool,
) {
    let groups = groups();
    let source = source(&groups);
    let mut debug = BTreeMap::new();
    for configuration in ["Debug", "Release"] {
        let (directory, images) = images(host, configuration, &source);
        let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
        for group in &groups {
            let result = managed
                .command()
                .arg(
                    directory
                        .path()
                        .join(format!("bin/{configuration}/net10.0/WideArithmetic.dll")),
                )
                .arg(format!("Managed{}", group.name))
                .output()
                .unwrap();
            assert!(
                result.status.success(),
                "{}: {}",
                group.name,
                String::from_utf8_lossy(&result.stderr)
            );
            let managed = String::from_utf8_lossy(&result.stdout);
            let expected = group
                .fault
                .map(|fault| format!("THROW:{fault}"))
                .unwrap_or_else(|| {
                    group
                        .expected()
                        .iter()
                        .map(|b| format!("{b:02X}"))
                        .collect()
                });
            assert_eq!(
                managed.trim(),
                expected,
                "managed {} {configuration}",
                group.name
            );
            let mut object = compile::object(&resolver, Some(&format!("Program::{}", group.name)))
                .unwrap_or_else(|e| panic!("{} {configuration}: {e}", group.name));
            let mut bank = 1;
            for section in &mut object.sections {
                if section.name.starts_with(".text.__koh_a") {
                    section.bank = Some(bank);
                    bank += 1;
                }
            }
            let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
            let linked = koh_link::link(
                &[object],
                &koh_link::LinkOptions {
                    cgb: if color {
                        koh_link::CgbMode::Compatible
                    } else {
                        koh_link::CgbMode::Preserve
                    },
                    ..Default::default()
                },
            )
            .unwrap();
            let report = execute_mode(
                host,
                directory.path(),
                &linked,
                if group.fault.is_some() {
                    "__koh_abort"
                } else {
                    "__koh_exit"
                },
                "--native-heap",
            );
            let fields: Vec<_> = report.split_whitespace().collect();
            assert_eq!(fields[3], "OK", "{} {configuration}", group.name);
            if color {
                assert_eq!(fields[5], "6B".repeat(4096), "{} bank2", group.name);
            }
            if group.fault.is_some() {
                assert_eq!(fields[0], "02", "{} fault", group.name);
                continue;
            }
            assert_eq!(fields[0], "00");
            assert_eq!(fields[2], "D000");
            let pointer = u32::from_str_radix(fields[1], 16).unwrap();
            let count = group.results() * group.bytes;
            assert!(
                (0xd000..=0xe000 - count as u32).contains(&pointer),
                "{} result pointer {pointer:04X}",
                group.name
            );
            let start = (pointer as usize - 0xd000) * 2;
            let actual = &fields[4][start..start + count * 2];
            assert_eq!(actual, expected, "native {} {configuration}", group.name);
            if configuration == "Debug" {
                debug.insert(group.name.clone(), actual.to_owned());
            } else {
                assert_eq!(
                    actual, debug[&group.name],
                    "{} build-mode order",
                    group.name
                );
            }
        }
    }
}

#[rstest]
#[case(false)]
#[case(true)]
fn legacy_generic_wide_expressions_match_complete_results(host: &Host, #[case] color: bool) {
    let source = r#"
public static unsafe class Program {
    static int Seed = 5;
    static int Negative = -5;
    static uint High = 0x80000000;
    static ulong Big = 0xF000000000000000UL;
    static byte[] Results;
    static long Arithmetic(int seed) {
        long a = seed;
        long b = 1234567890123L;
        long sum = a + b;
        long diff = b - a;
        long prod = a * 1000000L;
        long q = b / 1000000007L;
        long rem = b % 1000000007L;
        return sum + diff + prod + q + rem;
    }
    static long Conversion(int n, uint u) {
        long a = n;
        long b = u;
        return a * 1000000L + b;
    }
    static ulong Unsigned(ulong big) {
        ulong quotient = big / 16UL;
        ulong remainder = big % 16UL;
        return quotient + remainder;
    }
    public static int Main() {
        Results = new byte[24];
        fixed(byte* p = Results) {
            *(long*)p = Arithmetic(Seed);
            *(long*)(p + 8) = Conversion(Negative, High);
            *(ulong*)(p + 16) = Unsigned(Big);
            return (ushort)p;
        }
    }
}
"#;
    let b = 1_234_567_890_123i128;
    let expected: String = [
        (2 * b + 5_000_000 + b / 1_000_000_007 + b % 1_000_000_007) as u64,
        (2_147_483_648i128 - 5_000_000) as u64,
        0x0f00_0000_0000_0000,
    ]
    .into_iter()
    .flat_map(u64::to_le_bytes)
    .map(|byte| format!("{byte:02X}"))
    .collect();
    let mut debug = None;
    for configuration in ["Debug", "Release"] {
        let (directory, images) = images(host, configuration, source);
        let resolver = Resolver::new(&[&images[0], &images[1]]).unwrap();
        let mut object = compile::object(&resolver, Some("Program::Main")).unwrap();
        let mut bank = 1;
        for section in &mut object.sections {
            if section.name.starts_with(".text.__koh_a") {
                section.bank = Some(bank);
                bank += 1;
            }
        }
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let linked = koh_link::link(
            &[object],
            &koh_link::LinkOptions {
                cgb: if color {
                    koh_link::CgbMode::Compatible
                } else {
                    koh_link::CgbMode::Preserve
                },
                ..Default::default()
            },
        )
        .unwrap();
        let report = execute_mode(
            host,
            directory.path(),
            &linked,
            "__koh_exit",
            "--native-heap",
        );
        let fields: Vec<_> = report.split_whitespace().collect();
        assert_eq!((fields[0], fields[2], fields[3]), ("00", "D000", "OK"));
        if color {
            assert_eq!(fields[5], "6B".repeat(4096));
        }
        let pointer = u32::from_str_radix(fields[1], 16).unwrap();
        assert!((0xd000..=0xe000 - 24).contains(&pointer));
        let start = (pointer as usize - 0xd000) * 2;
        let actual = &fields[4][start..start + 48];
        assert_eq!(actual, expected, "{configuration}");
        if configuration == "Debug" {
            debug = Some(actual.to_owned());
        } else {
            assert_eq!(Some(actual), debug.as_deref());
        }
    }
}
