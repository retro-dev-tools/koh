use crate::{
    ir::*,
    resolve::{Definition, Resolver},
};

pub(crate) fn guard(constructor: Definition) -> Op {
    Op::Call {
        target: Definition {
            assembly: constructor.assembly,
            token: 0xff000000 | (constructor.token & 0xffffff),
        },
        signature: Signature {
            parameters: vec![],
            returns: None,
        },
        arguments: vec![],
    }
}

pub(crate) fn function(resolver: &Resolver<'_>, identity: Definition) -> Result<Function, String> {
    let constructor = Definition {
        assembly: identity.assembly,
        token: 0x06000000 | (identity.token & 0xffffff),
    };
    let image = resolver.assembly(constructor.assembly)?;
    let signature = image.method_signature(constructor.token)?;
    let row = image.method(constructor.token)?;
    let owner = image
        .metadata()
        .type_defs
        .partition_point(|ty| ty.method_list <= constructor.token & 0xffffff)
        as u32;
    if image
        .metadata()
        .strings
        .get(row.name)
        .map_err(|e| e.to_string())?
        != ".cctor"
        || row.flags & 0x1810 != 0x1810
        || signature.calling_convention.0 != 0
        || signature.generic_param_count != 0
        || !signature.params.is_empty()
        || signature.return_type != clrmeta::TypeSig::Primitive(clrmeta::ElementType::Void)
        || image
            .metadata()
            .generic_params
            .iter()
            .any(|p| p.owner.table == Some(clrmeta::TableId::TypeDef) && p.owner.row == owner)
    {
        return Err(format!(
            "Invalid or generic type/module constructor {constructor:?}"
        ));
    }
    let instruction = |result, op| Instruction {
        result,
        op,
        il_offset: 0,
    };
    let finish = || Terminator {
        kind: Control::Return(None),
        il_offset: 0,
    };
    let f = Function {
        identity,
        parameters: vec![],
        returns: None,
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![],
                instructions: vec![
                    instruction(Some((0, Type::Native)), Op::InitializerAddress(constructor)),
                    instruction(
                        Some((1, Type::I32)),
                        Op::LoadMemory {
                            address: 0,
                            bits: 8,
                            signed: false,
                        },
                    ),
                ],
                terminator: Terminator {
                    kind: Control::Branch {
                        condition: 1,
                        yes: Edge {
                            block: 2,
                            arguments: vec![],
                        },
                        no: Edge {
                            block: 1,
                            arguments: vec![],
                        },
                    },
                    il_offset: 0,
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![
                    instruction(Some((2, Type::I32)), Op::Constant(1)),
                    instruction(
                        None,
                        Op::StoreMemory {
                            address: 0,
                            value: 2,
                            bits: 8,
                        },
                    ),
                    instruction(
                        None,
                        Op::Call {
                            target: constructor,
                            signature: Signature {
                                parameters: vec![],
                                returns: None,
                            },
                            arguments: vec![],
                        },
                    ),
                    instruction(Some((3, Type::I32)), Op::Constant(2)),
                    instruction(
                        None,
                        Op::StoreMemory {
                            address: 0,
                            value: 3,
                            bits: 8,
                        },
                    ),
                ],
                terminator: finish(),
            },
            Block {
                parameters: vec![],
                instructions: vec![],
                terminator: finish(),
            },
        ],
    };
    verify(&f)?;
    Ok(f)
}
