use super::Builder;
use crate::{
    generics::Argument,
    ir::{ArrayKind, Type},
    resolve::{ResolvedMethod, Resolver},
};
use clrmeta::{ElementType, TypeSig};

fn bound_signature(
    resolver: &Resolver<'_>,
    method: crate::resolve::Definition,
) -> Result<(Vec<Argument>, Argument), String> {
    let instance = resolver.instance(method)?;
    let raw = instance.as_ref().map_or(method, |i| i.method);
    let arguments = instance
        .as_ref()
        .map_or(&[][..], |i| i.arguments.as_slice());
    let signature = resolver
        .assembly(raw.assembly)?
        .method_signature(raw.token)?;
    let params = signature
        .params
        .iter()
        .map(|ty| resolver.bind_signature(raw.assembly, ty, arguments, &[]))
        .collect::<Result<Vec<_>, _>>()?;
    Ok((
        params,
        resolver.bind_signature(raw.assembly, &signature.return_type, arguments, &[])?,
    ))
}

fn same(resolver: &Resolver<'_>, left: &Argument, right: &Argument) -> Result<bool, String> {
    resolver.types_equal(left.assembly, &left.ty, right.assembly, &right.ty, 0)
}

pub(super) fn target(
    resolver: &Resolver<'_>,
    builder: &Builder<'_, '_>,
    original: &ResolvedMethod,
) -> Result<Option<ResolvedMethod>, String> {
    let owner = super::owner_type(resolver, original.definition)?;
    if resolver.framework_definition(owner)? != Some(("System.Linq", "Enumerable")) {
        return Ok(None);
    }
    let image = resolver.assembly(original.definition.assembly)?;
    let method = image.method(original.definition.token)?;
    let name = image
        .metadata()
        .strings
        .get(method.name)
        .map_err(|e| e.to_string())?;
    let signature = image.method_signature(original.definition.token)?;
    if method.flags & 0x17 != 0x16
        || signature.calling_convention.has_this()
        || signature.sentinel.is_some()
    {
        return Err(format!("Unsupported LINQ declaration {name}"));
    }
    let call = resolver.instantiate_in(
        original,
        &builder.method_arguments,
        &builder.type_arguments,
        builder.declaring,
    )?;
    let (parameters, returns) = bound_signature(resolver, call)?;
    if parameters.is_empty() || parameters.len() > 2 {
        return Err(format!("Unsupported LINQ overload {name}"));
    }
    let source = builder
        .stack
        .len()
        .checked_sub(parameters.len())
        .and_then(|i| builder.stack.get(i))
        .ok_or("LINQ call evaluation stack underflow")?;
    let array = matches!(source.1, Type::Array(_, ArrayKind::Vector));
    let numeric = matches!(name, "Sum" | "Min" | "Max");
    let mut operation = match name {
        "Where" | "Select" if parameters.len() == 2 => name.to_ascii_lowercase(),
        "Count" | "Any" => format!(
            "{}{}",
            name.to_ascii_lowercase(),
            if parameters.len() == 2 {
                ".predicate"
            } else {
                ""
            }
        ),
        "All" if parameters.len() == 2 => "all".into(),
        "Sum" | "Min" | "Max" => {
            let TypeSig::Primitive(element) = returns.ty else {
                return Err(format!(
                    "Unsupported LINQ {name} result type {:?}",
                    returns.ty
                ));
            };
            let width = match element {
                ElementType::I1 => "i8",
                ElementType::U1 => "u8",
                ElementType::I2 => "i16",
                ElementType::U2 => "u16",
                ElementType::I4 => "i32",
                ElementType::U4 => "u32",
                ElementType::I8 => "i64",
                ElementType::U8 => "u64",
                ElementType::R4 => "f32",
                ElementType::R8 => "f64",
                ElementType::IntPtr => "isize",
                ElementType::UIntPtr => "usize",
                _ => return Err(format!("Unsupported LINQ {name} numeric type {element:?}")),
            };
            if name == "Sum" && !matches!(width, "i32" | "i64" | "f32" | "f64") {
                return Err(format!("Unsupported LINQ Sum overload for {element:?}"));
            }
            format!(
                "{}.{width}{}",
                name.to_ascii_lowercase(),
                if parameters.len() == 2 { ".select" } else { "" }
            )
        }
        _ => return Err(format!("Unsupported LINQ operator or overload {name}")),
    };
    operation.push_str(if array { ".array" } else { ".sequence" });
    let key = format!("linq.{operation}");
    let definition = crate::contracts::runtime_implementation(resolver, &key)?;
    let runtime_sig = resolver
        .assembly(definition.assembly)?
        .method_signature(definition.token)?;
    let count = runtime_sig.generic_param_count as usize;
    let method_arguments = if count == original.method_arguments.len() {
        original.method_arguments.clone()
    } else if count == 0 {
        vec![]
    } else if numeric && parameters.len() == 2 && count == 1 && original.method_arguments.len() == 2
    {
        original.method_arguments[..1].to_vec()
    } else {
        return Err(format!(
            "Unsupported LINQ generic shape for {name}; KohRuntime {key} has incompatible arity"
        ));
    };
    let runtime = ResolvedMethod {
        definition,
        type_arguments: vec![],
        method_arguments,
        argument_assembly: original.argument_assembly,
        declaring: None,
    };
    let instantiated = resolver.instantiate_in(
        &runtime,
        &builder.method_arguments,
        &builder.type_arguments,
        builder.declaring,
    )?;
    let (expected, result) = bound_signature(resolver, instantiated)?;
    let mismatch =
        || format!("Unsupported LINQ overload {name}: KohRuntime {key} signature differs");
    if expected.len() != parameters.len() || !same(resolver, &returns, &result)? {
        return Err(mismatch());
    }
    for (index, (left, right)) in parameters.iter().zip(&expected).enumerate() {
        if index == 0 && array {
            let TypeSig::Class(encoded) = left.ty else {
                return Err(mismatch());
            };
            let source = resolver.signature_type(left.assembly, encoded)?;
            if resolver.framework_definition(source)?
                != Some(("System.Collections.Generic", "IEnumerable`1"))
            {
                return Err(mismatch());
            }
            let args = resolver
                .type_instance(source)?
                .ok_or_else(mismatch)?
                .arguments;
            let TypeSig::SzArray(element) = &right.ty else {
                return Err(mismatch());
            };
            let element = Argument {
                assembly: right.assembly,
                ty: (**element).clone(),
            };
            if args.len() != 1 || !same(resolver, &args[0], &element)? {
                return Err(mismatch());
            }
        } else if !same(resolver, left, right)? {
            return Err(mismatch());
        }
    }
    Ok(Some(runtime))
}
