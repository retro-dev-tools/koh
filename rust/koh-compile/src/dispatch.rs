use crate::{
    ir::*,
    lower,
    resolve::{Definition, Resolver},
};
use std::collections::HashMap;

pub(crate) fn identity(method: Definition) -> Definition {
    Definition {
        token: 0xfe000000 | (method.token & 0xffffff),
        ..method
    }
}

pub(crate) fn declaration(method: Definition) -> Definition {
    Definition {
        token: 0x06000000 | (method.token & 0xffffff),
        ..method
    }
}

pub(crate) struct Binder<'r, 'a> {
    resolver: &'r Resolver<'a>,
    slots: HashMap<Definition, Definition>,
    work: usize,
}

impl<'r, 'a> Binder<'r, 'a> {
    pub(crate) fn new(resolver: &'r Resolver<'a>) -> Self {
        Self {
            resolver,
            slots: HashMap::new(),
            work: 0,
        }
    }

    fn charge(&mut self, amount: usize) -> Result<(), String> {
        self.work = self
            .work
            .checked_add(amount)
            .ok_or("Virtual binding work overflow")?;
        if self.work > 1_048_576 {
            return Err("Virtual binding work exceeds1048576".into());
        }
        Ok(())
    }

    fn chain(&mut self, mut class: ClassType) -> Result<Vec<Definition>, String> {
        let mut result = Vec::new();
        while let Some(id) = class.identity() {
            self.charge(1)?;
            if result.len() >= 64 {
                return Err("Virtual inheritance cycle/depth exceeds64".into());
            }
            result.push(id);
            class = match class.base() {
                Some(base) => lower::class_type(self.resolver, base)?,
                None => ClassType::root(),
            };
        }
        Ok(result)
    }

    fn methods(&mut self, owner: Definition) -> Result<Vec<Definition>, String> {
        let metadata = self.resolver.assembly(owner.assembly)?.metadata();
        let index = (owner.token & 0xffffff)
            .checked_sub(1)
            .ok_or("Invalid virtual owner")? as usize;
        let ty = metadata
            .type_defs
            .get(index)
            .ok_or("Invalid virtual owner")?;
        if metadata
            .method_impls
            .iter()
            .any(|m| m.class == (index + 1) as u32)
        {
            return Err("Explicit MethodImpl virtual mappings require dispatch binding".into());
        }
        let end = metadata
            .type_defs
            .get(index + 1)
            .map(|t| t.method_list)
            .unwrap_or(metadata.method_defs.len() as u32 + 1);
        if ty.method_list == 0
            || ty.method_list > end
            || end > metadata.method_defs.len() as u32 + 1
        {
            return Err("Invalid virtual method range".into());
        }
        self.charge((end - ty.method_list) as usize + metadata.method_impls.len())?;
        Ok((ty.method_list..end)
            .map(|row| Definition {
                assembly: owner.assembly,
                token: 0x06000000 | row,
            })
            .collect())
    }

    fn signature(&mut self, method: Definition) -> Result<clrmeta::MethodSig, String> {
        let image = self.resolver.assembly(method.assembly)?;
        let row = image.method(method.token)?;
        self.charge(
            image
                .metadata()
                .blobs
                .get(row.signature)
                .map_err(|e| e.to_string())?
                .len()
                + 1,
        )?;
        image.method_signature(method.token)
    }

    fn slot(&mut self, method: Definition) -> Result<Definition, String> {
        if let Some(slot) = self.slots.get(&method) {
            return Ok(*slot);
        }
        self.charge(1)?;
        let image = self.resolver.assembly(method.assembly)?;
        let flags = image.method(method.token)?.flags;
        let signature = self.signature(method)?;
        if flags & 0x50 != 0x40
            || !signature.calling_convention.has_this()
            || signature.generic_param_count != 0
        {
            return Err("Virtual slot must be a nongeneric instance virtual method".into());
        }
        let owner = lower::owner_type(self.resolver, method)?;
        let class = lower::class_type(self.resolver, owner)?;
        let name = image
            .metadata()
            .strings
            .get(image.method(method.token)?.name)
            .map_err(|e| e.to_string())?;
        let mut slot = method;
        if flags & 0x100 == 0 {
            for base in self.chain(class)?.into_iter().skip(1) {
                let mut matched = None;
                for candidate in self.methods(base)? {
                    let other = self.resolver.assembly(candidate.assembly)?;
                    let row = other.method(candidate.token)?;
                    if row.flags & 0x50 != 0x40
                        || other
                            .metadata()
                            .strings
                            .get(row.name)
                            .map_err(|e| e.to_string())?
                            != name
                    {
                        continue;
                    }
                    let other_signature = self.signature(candidate)?;
                    if !self.resolver.methods_equal(
                        method.assembly,
                        &signature,
                        candidate.assembly,
                        &other_signature,
                        0,
                    )? {
                        continue;
                    }
                    if matched.replace(candidate).is_some() {
                        return Err("Ambiguous inherited virtual slot".into());
                    }
                    if row.flags & 0x20 != 0 {
                        return Err("Virtual override targets a final method".into());
                    }
                    if row.flags & 0x200 != 0
                        && !match row.flags & 7 {
                            2 | 3 => owner.assembly == base.assembly,
                            4..=6 => true,
                            _ => false,
                        }
                    {
                        return Err("Virtual override targets an inaccessible strict method".into());
                    }
                }
                if let Some(parent) = matched {
                    slot = self.slot(parent)?;
                    break;
                }
            }
        }
        self.slots.insert(method, slot);
        Ok(slot)
    }

    pub(crate) fn implementation(
        &mut self,
        method: Definition,
        class: ClassType,
    ) -> Result<Option<Definition>, String> {
        self.charge(1)?;
        let owner = lower::owner_type(self.resolver, method)?;
        let chain = self.chain(class)?;
        if !chain.contains(&owner) {
            return Ok(None);
        }
        let slot = self.slot(method)?;
        let signature = self.signature(method)?;
        let image = self.resolver.assembly(method.assembly)?;
        let name = image
            .metadata()
            .strings
            .get(image.method(method.token)?.name)
            .map_err(|e| e.to_string())?;
        for owner in chain {
            let mut implementation = None;
            for candidate in self.methods(owner)? {
                let image = self.resolver.assembly(candidate.assembly)?;
                let row = image.method(candidate.token)?;
                if image
                    .metadata()
                    .strings
                    .get(row.name)
                    .map_err(|e| e.to_string())?
                    != name
                {
                    continue;
                }
                let candidate_signature = self.signature(candidate)?;
                if !self.resolver.methods_equal(
                    method.assembly,
                    &signature,
                    candidate.assembly,
                    &candidate_signature,
                    0,
                )? {
                    continue;
                }
                if row.flags & 0x50 == 0x40 && self.slot(candidate)? == slot {
                    if row.flags & 0x400 != 0 {
                        return Err("Concrete class has no virtual implementation".into());
                    }
                    if implementation.replace(candidate).is_some() {
                        return Err("Ambiguous virtual implementations in one class".into());
                    }
                }
            }
            if implementation.is_some() {
                return Ok(implementation);
            }
        }
        Err("Virtual slot has no implementation".into())
    }

    fn typed_signature(&mut self, method: Definition) -> Result<Signature, String> {
        let signature = self.signature(method)?;
        lower::bound_signature(
            self.resolver,
            method.assembly,
            &lower::storage_signature(self.resolver, method, &signature)?,
        )
    }

    pub(crate) fn function(
        &mut self,
        method: Definition,
        classes: &[ClassType],
    ) -> Result<Function, String> {
        self.slot(method)?;
        let signature = self.typed_signature(method)?;
        let mut cases = Vec::new();
        for &class in classes {
            if let Some(target) = self.implementation(method, class)? {
                cases.push((class, target));
            }
        }
        let mut next = signature.parameters.len() as u32;
        let parameters: Vec<_> = signature
            .parameters
            .iter()
            .enumerate()
            .map(|(id, &ty)| (id as u32, ty))
            .collect();
        let mut blocks = Vec::new();
        let instruction = |result, op| Instruction {
            result,
            op,
            il_offset: 0,
        };
        for (index, (class, target)) in cases.iter().enumerate() {
            let condition = next;
            next += 1;
            let mut tests = vec![instruction(
                Some((condition, Type::I32)),
                Op::IsClass {
                    object: 0,
                    class: *class,
                },
            )];
            if index == 0 {
                tests.insert(0, instruction(None, Op::CheckObject(0)));
            }
            blocks.push(Block {
                parameters: if index == 0 {
                    parameters.clone()
                } else {
                    vec![]
                },
                instructions: tests,
                terminator: Terminator {
                    il_offset: 0,
                    kind: Control::Branch {
                        condition,
                        yes: Edge {
                            block: index * 2 + 1,
                            arguments: vec![],
                        },
                        no: Edge {
                            block: index * 2 + 2,
                            arguments: vec![],
                        },
                    },
                },
            });
            let target_signature = self.typed_signature(*target)?;
            if target_signature.parameters[1..] != signature.parameters[1..]
                || target_signature.returns != signature.returns
            {
                return Err("Virtual implementation signature differs from slot".into());
            }
            let Type::Object(owner) = target_signature.parameters[0] else {
                return Err("Virtual receiver must be a reference class".into());
            };
            let receiver = next;
            next += 1;
            let mut arguments: Vec<_> = (0..signature.parameters.len() as u32).collect();
            arguments[0] = receiver;
            let result = signature.returns.map(|ty| {
                let id = next;
                next += 1;
                (id, ty)
            });
            blocks.push(Block {
                parameters: vec![],
                instructions: vec![
                    instruction(
                        Some((receiver, Type::Object(owner))),
                        Op::CastClass {
                            object: 0,
                            class: owner,
                        },
                    ),
                    instruction(
                        result,
                        Op::Call {
                            target: *target,
                            signature: target_signature,
                            arguments,
                        },
                    ),
                ],
                terminator: Terminator {
                    il_offset: 0,
                    kind: Control::Return(result.map(|p| p.0)),
                },
            });
        }
        blocks.push(Block {
            parameters: if cases.is_empty() { parameters } else { vec![] },
            instructions: if cases.is_empty() {
                vec![instruction(None, Op::CheckObject(0))]
            } else {
                vec![]
            },
            terminator: Terminator {
                il_offset: 0,
                kind: Control::TypeFault,
            },
        });
        let function = Function {
            identity: identity(method),
            parameters: signature.parameters,
            returns: signature.returns,
            locals: vec![],
            blocks,
        };
        verify(&function)?;
        Ok(function)
    }
}
