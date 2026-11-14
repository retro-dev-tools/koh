use clrmeta::{ElementType as E, TypeSig as T};
use koh_compile::signature;
use rstest::rstest;

#[rstest]
#[case(0x02, E::Boolean)]
#[case(0x03, E::Char)]
#[case(0x04, E::I1)]
#[case(0x05, E::U1)]
#[case(0x06, E::I2)]
#[case(0x07, E::U2)]
#[case(0x08, E::I4)]
#[case(0x09, E::U4)]
#[case(0x0a, E::I8)]
#[case(0x0b, E::U8)]
#[case(0x0c, E::R4)]
#[case(0x0d, E::R8)]
#[case(0x0e, E::String)]
#[case(0x18, E::IntPtr)]
#[case(0x19, E::UIntPtr)]
#[case(0x1c, E::Object)]
fn primitive_field_types(#[case] encoding: u8, #[case] expected: E) {
    assert_eq!(
        signature::field(&[6, encoding]).unwrap(),
        T::Primitive(expected)
    );
}

#[rstest]
#[case(&[0x00], 0)]
#[case(&[0x06], 3)]
#[case(&[0x7f], -1)]
#[case(&[0x7b], -3)]
#[case(&[0x01], -64)]
#[case(&[0x80, 0x80], 64)]
#[case(&[0xbf, 0x7f], -65)]
#[case(&[0x80, 0x01], -8192)]
#[case(&[0xc0, 0x00, 0x00, 0x01], -268435456)]
#[case(&[0xdf, 0xff, 0xff, 0xfe], 268435455)]
fn array_bounds_use_ecma_signed_compression(#[case] bound: &[u8], #[case] expected: i32) {
    let mut bytes = vec![0x14, 0x08, 1, 0, 1];
    bytes.extend_from_slice(bound);
    assert_eq!(
        signature::type_spec(&bytes).unwrap(),
        T::Array {
            element_type: Box::new(T::Primitive(E::I4)),
            rank: 1,
            sizes: vec![],
            lo_bounds: vec![expected],
        }
    );
}

#[test]
fn method_preserves_receiver_generic_arity_and_parameters() {
    let method = signature::method(&[0x30, 1, 2, 0x1e, 0, 0x10, 0x08, 0x1d, 0x05]).unwrap();
    assert_eq!(method.calling_convention.0, 0x30);
    assert_eq!(method.generic_param_count, 1);
    assert_eq!(method.return_type, T::MVar(0));
    assert_eq!(
        method.params,
        vec![
            T::ByRef(Box::new(T::Primitive(E::I4))),
            T::SzArray(Box::new(T::Primitive(E::U1)))
        ]
    );
}

#[test]
fn preserves_vararg_boundary() {
    let method = signature::method(&[5, 2, 1, 8, 0x41, 0x0a]).unwrap();
    assert_eq!(method.sentinel, Some(1));
    assert_eq!(
        method.params,
        vec![T::Primitive(E::I4), T::Primitive(E::I8)]
    );
}

#[test]
fn preserves_pointer_modifiers_and_pinned_locals() {
    assert_eq!(
        signature::field(&[6, 0x0f, 1]).unwrap(),
        T::Ptr(Box::new(T::Primitive(E::Void)))
    );
    assert_eq!(
        signature::locals(&[7, 1, 0x1f, 5, 0x45, 0x10, 8]).unwrap(),
        vec![T::Modified {
            required: true,
            modifier: 5,
            inner: Box::new(T::Pinned(Box::new(T::ByRef(Box::new(T::Primitive(E::I4)))))),
        }]
    );
}

#[test]
fn preserves_generic_instantiation_and_function_pointer() {
    assert_eq!(
        signature::type_spec(&[0x15, 0x11, 4, 2, 0x13, 0, 0x1e, 0]).unwrap(),
        T::GenericInst {
            is_value_type: true,
            type_ref: 4,
            type_args: vec![T::Var(0), T::MVar(0)],
        }
    );
    let T::FnPtr(method) = signature::type_spec(&[0x1b, 1, 1, 1, 8]).unwrap() else {
        panic!("function pointer")
    };
    assert_eq!(method.calling_convention.0, 1);
    assert_eq!(method.return_type, T::Primitive(E::Void));
    assert_eq!(method.params, vec![T::Primitive(E::I4)]);
    assert_eq!(
        signature::method_spec(&[0x0a, 2, 0x08, 0x0b]).unwrap(),
        vec![T::Primitive(E::I4), T::Primitive(E::U8)]
    );
}

#[rstest]
#[case(&[])]
#[case(&[6])]
#[case(&[6, 1])]
#[case(&[6, 8, 8])]
#[case(&[6, 0x10, 1])]
#[case(&[6, 0x10, 0x10, 8])]
#[case(&[6, 0x16])]
#[case(&[6, 0x45, 8])]
#[case(&[6, 0x1d, 1])]
#[case(&[6, 0x12, 0])]
#[case(&[6, 0x12, 7])]
#[case(&[6, 0x15, 0x08, 4, 1, 8])]
#[case(&[6, 0x15, 0x12, 4, 0])]
#[case(&[6, 0x14, 8, 0, 0, 0])]
#[case(&[6, 0x14, 8, 1, 2, 1, 1, 0])]
#[case(&[6, 0x14, 8, 1, 0, 2, 0, 0])]
#[case(&[6, 0x13, 0x80, 0])]
#[case(&[6, 0x13, 0xe0])]
fn malformed_fields_fail(#[case] bytes: &[u8]) {
    let error = signature::field(bytes).unwrap_err();
    assert!(error.contains("signature byte"), "{error}");
}

#[rstest]
#[case(&[0x80, 0, 1])]
#[case(&[0x40, 0, 1])]
#[case(&[0x10, 0, 0, 1])]
#[case(&[0, 1, 1, 1])]
#[case(&[0, 1, 1, 0x41, 8])]
#[case(&[5, 2, 1, 0x41, 8, 0x41, 8])]
#[case(&[0, 0, 1, 0])]
#[case(&[0, 0xdf, 0xff, 0xff, 0xff, 1])]
fn malformed_methods_fail(#[case] bytes: &[u8]) {
    assert!(signature::method(bytes).is_err());
}

#[test]
fn bounds_allocation_and_recursion_before_parsing() {
    assert!(signature::locals(&[7, 0xdf, 0xff, 0xff, 0xff]).is_err());
    let mut nested = vec![6];
    nested.extend_from_slice(&[0x1d; 1000]);
    nested.push(8);
    assert!(signature::field(&nested).unwrap_err().contains("nesting"));
    assert!(
        signature::field(&vec![8; 65537])
            .unwrap_err()
            .contains("64 KiB")
    );
}

#[test]
fn ref_fields_are_valid_signature_shapes() {
    assert_eq!(
        signature::field(&[6, 0x10, 8]).unwrap(),
        T::ByRef(Box::new(T::Primitive(E::I4)))
    );
}

#[rstest]
#[case(&[1, 0, 1])]
#[case(&[0x60, 0, 1])]
#[case(&[5, 1, 1, 0x41, 8])]
#[case(&[0x15, 1, 0, 1])]
fn method_definitions_reject_callsite_only_shapes(#[case] bytes: &[u8]) {
    signature::method(bytes).unwrap();
    assert!(signature::method_definition(bytes).is_err());
}

#[test]
fn arbitrary_and_truncated_signatures_do_not_panic() {
    let mut state = 0x0123456789abcdefu64;
    for length in 0..128 {
        let bytes: Vec<_> = (0..length)
            .map(|_| {
                state = state.wrapping_mul(6364136223846793005).wrapping_add(1);
                (state >> 32) as u8
            })
            .collect();
        for prefix in 0..=bytes.len() {
            let input = &bytes[..prefix];
            let _ = signature::method(input);
            let _ = signature::method_definition(input);
            let _ = signature::field(input);
            let _ = signature::locals(input);
            let _ = signature::type_spec(input);
            let _ = signature::method_spec(input);
        }
    }
}
