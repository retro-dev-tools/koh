use crate::{
    ir::*,
    lower,
    resolve::{Definition, Resolver},
};
use std::collections::{HashMap, HashSet};

pub(crate) fn identity(method: Definition) -> Definition {
    Definition {
        token: if method.token >> 24 == 0xfb {
            0xfa000000
        } else {
            0xfe000000
        } | (method.token & 0xffffff),
        ..method
    }
}

pub(crate) fn declaration(method: Definition) -> Definition {
    Definition {
        token: if method.token >> 24 == 0xfa {
            0xfb000000
        } else {
            0x06000000
        } | (method.token & 0xffffff),
        ..method
    }
}

pub(crate) fn is_dispatch(method: Definition) -> bool {
    matches!(method.token >> 24, 0xfa | 0xfe)
}

#[cfg(test)]
mod tests {
    use super::{declaration, identity, is_dispatch};
    use crate::resolve::Definition;
    use rstest::rstest;

    #[rstest]
    #[case(0, 0x06000001)]
    #[case(2, 0x060000ff)]
    #[case(0, 0xfb000001)]
    #[case(2, 0xfb000fff)]
    fn dispatch_identity_retains_closed_target_scope(#[case] assembly: usize, #[case] token: u32) {
        let method = Definition { assembly, token };
        let dispatch = identity(method);
        assert!(is_dispatch(dispatch));
        assert_eq!(declaration(dispatch), method);
        assert_ne!(
            identity(Definition {
                token: 0x06000000 | (token & 0xffffff),
                assembly
            }),
            identity(Definition {
                token: 0xfb000000 | (token & 0xffffff),
                assembly
            })
        );
    }
}

pub(crate) struct Binder<'r, 'a> {
    resolver: &'r Resolver<'a>,
    slots: HashMap<Definition, Definition>,
    mappings: HashMap<Definition, Vec<(Definition, Definition)>>,
    active: HashSet<(Definition, ClassType)>,
    work: usize,
}

impl<'r, 'a> Binder<'r, 'a> {
    pub(crate) fn new(resolver: &'r Resolver<'a>) -> Self {
        Self {
            resolver,
            slots: HashMap::new(),
            mappings: HashMap::new(),
            active: HashSet::new(),
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

    fn mappings(&mut self, owner: Definition) -> Result<Vec<(Definition, Definition)>, String> {
        if let Some(result) = self.mappings.get(&owner) {
            let result = result.clone();
            self.charge(result.len())?;
            return Ok(result);
        }
        let metadata = self.resolver.assembly(owner.assembly)?.metadata();
        self.charge(metadata.method_impls.len())?;
        let class = lower::class_type(self.resolver, owner)?;
        let chain = self.chain(class)?;
        let mut result = Vec::new();
        for row in metadata
            .method_impls
            .iter()
            .filter(|row| row.class == owner.token & 0xffffff)
        {
            let mut targets = Vec::new();
            for token in [row.method_declaration, row.method_body] {
                let table = token.table.ok_or("MethodImpl has a null method")?;
                if !matches!(
                    table,
                    clrmeta::TableId::MethodDef | clrmeta::TableId::MemberRef
                ) || token.row == 0
                {
                    return Err("MethodImpl requires MethodDef or MemberRef".into());
                }
                let resolved = self
                    .resolver
                    .resolve_method(owner.assembly, ((table as u32) << 24) | token.row)?;
                if !resolved.type_arguments.is_empty() || !resolved.method_arguments.is_empty() {
                    return Err("Generic MethodImpl requires instantiated binding".into());
                }
                targets.push(resolved.definition);
            }
            let (declaration, body) = (targets[0], targets[1]);
            let declared_owner = lower::owner_type(self.resolver, declaration)?;
            let body_owner = lower::owner_type(self.resolver, body)?;
            let declared_class = lower::class_type(self.resolver, declared_owner)?;
            if !chain.contains(&body_owner)
                || if declared_class.is_interface() {
                    !crate::layout::implements(self.resolver, owner, declared_owner)?
                } else {
                    !chain.contains(&declared_owner)
                }
            {
                return Err("MethodImpl body/declaration is outside the class inheritance or interface contract".into());
            }
            let declaration_row = self
                .resolver
                .assembly(declaration.assembly)?
                .method(declaration.token)?;
            let body_row = self.resolver.assembly(body.assembly)?.method(body.token)?;
            let reabstract = class.is_interface() && body_row.flags & 0x400 != 0;
            if class.is_interface() && body_row.flags & 0x170 != 0x60 {
                return Err(
                    "Interface MethodImpl body must be final instance virtual without newslot"
                        .into(),
                );
            }
            if reabstract && body_row.rva != 0 {
                return Err(
                    "Abstract interface MethodImpl body cannot contain executable IL".into(),
                );
            }
            if body_row.rva == 0 && !reabstract {
                return Err("MethodImpl body must define an executable IL body".into());
            }
            if declaration_row.flags & 0x200 != 0
                && !match declaration_row.flags & 7 {
                    1 => declared_owner == owner,
                    2 | 3 => declared_owner.assembly == owner.assembly,
                    4..=6 => true,
                    _ => false,
                }
            {
                return Err("MethodImpl targets an inaccessible strict declaration".into());
            }
            if declaration_row.flags & 0x70 != 0x40 || body_row.flags & 0x50 != 0x40 {
                return Err("MethodImpl requires nonfinal instance virtual declaration and instance virtual body".into());
            }
            let declaration_sig = self.signature(declaration)?;
            let body_sig = self.signature(body)?;
            if !declaration_sig.calling_convention.has_this()
                || !body_sig.calling_convention.has_this()
                || !self.resolver.methods_equal(
                    declaration.assembly,
                    &declaration_sig,
                    body.assembly,
                    &body_sig,
                    0,
                )?
            {
                return Err(
                    "MethodImpl signatures differ; covariant or generic adaptation is unsupported"
                        .into(),
                );
            }
            if result.iter().any(|&(prior, _)| prior == declaration) {
                return Err("Duplicate MethodImpl declaration".into());
            }
            result.push((declaration, body));
        }
        self.mappings.insert(owner, result.clone());
        Ok(result)
    }

    fn declares_interface(
        &mut self,
        owner: Definition,
        interface: Definition,
    ) -> Result<bool, String> {
        let metadata = self.resolver.assembly(owner.assembly)?.metadata();
        self.charge(metadata.interface_impls.len())?;
        for row in metadata
            .interface_impls
            .iter()
            .filter(|row| row.class == owner.token & 0xffffff)
        {
            let table = row.interface.table.ok_or("Null InterfaceImpl")?;
            let id = self
                .resolver
                .resolve_type(owner.assembly, ((table as u32) << 24) | row.interface.row)?;
            if id == interface || crate::layout::implements(self.resolver, id, interface)? {
                return Ok(true);
            }
        }
        Ok(false)
    }

    fn interface_implementation(
        &mut self,
        method: Definition,
        class: ClassType,
        owner: Definition,
    ) -> Result<Option<Definition>, String> {
        let id = class
            .identity()
            .ok_or("Interface implementation requires a concrete class")?;
        if !crate::layout::implements(self.resolver, id, owner)? {
            return Ok(None);
        }
        self.slot(method)?;
        if self
            .resolver
            .assembly(method.assembly)?
            .method(method.token)?
            .flags
            & 0x20
            != 0
        {
            return Ok(Some(method));
        }
        let signature = self.signature(method)?;
        let image = self.resolver.assembly(method.assembly)?;
        let name = image
            .metadata()
            .strings
            .get(image.method(method.token)?.name)
            .map_err(|e| e.to_string())?;
        let chain = self.chain(class)?;
        for (index, &owner) in chain.iter().enumerate() {
            let explicit = self.mappings(owner)?;
            if let Some((_, body)) = explicit
                .iter()
                .find(|(declaration, _)| *declaration == method)
            {
                return self.implementation(*body, class);
            }
            if !self.declares_interface(owner, lower::owner_type(self.resolver, method)?)? {
                continue;
            }
            for &base in &chain[index..] {
                if let Some((_, body)) = self
                    .mappings(base)?
                    .into_iter()
                    .find(|(declaration, _)| *declaration == method)
                {
                    return self.implementation(body, class);
                }
                let mut candidate = None;
                for target in self.methods(base)? {
                    let image = self.resolver.assembly(target.assembly)?;
                    let row = image.method(target.token)?;
                    if row.flags & 0x57 != 0x46
                        || image
                            .metadata()
                            .strings
                            .get(row.name)
                            .map_err(|e| e.to_string())?
                            != name
                    {
                        continue;
                    }
                    let other = self.signature(target)?;
                    if self.resolver.methods_equal(
                        method.assembly,
                        &signature,
                        target.assembly,
                        &other,
                        0,
                    )? {
                        if candidate.replace(target).is_some() {
                            return Err("Ambiguous interface implementation".into());
                        }
                    }
                }
                if let Some(target) = candidate {
                    return self.implementation(target, class);
                }
            }
            break;
        }
        self.default_implementation(method, id).map(Some)
    }

    fn default_implementation(
        &mut self,
        method: Definition,
        class: Definition,
    ) -> Result<Definition, String> {
        let owner = lower::owner_type(self.resolver, method)?;
        let mut candidates = Vec::new();
        if self
            .resolver
            .assembly(method.assembly)?
            .method(method.token)?
            .flags
            & 0x400
            == 0
        {
            candidates.push((owner, method));
        }
        let layout = crate::layout::class(self.resolver, class)?;
        self.charge(layout.interfaces.len())?;
        for &interface in &layout.interfaces {
            for (declaration, body) in self.mappings(interface)? {
                if declaration == method {
                    candidates.push((interface, body));
                }
            }
        }
        let mut selected = None;
        for &(owner, body) in &candidates {
            let mut shadowed = false;
            for &(other, _) in &candidates {
                self.charge(1)?;
                if other != owner && crate::layout::implements(self.resolver, other, owner)? {
                    shadowed = true;
                    break;
                }
            }
            if !shadowed && selected.replace(body).is_some() {
                return Err("Ambiguous default interface implementation".into());
            }
        }
        let selected = selected.ok_or("Interface slot has no implementation")?;
        if self
            .resolver
            .assembly(selected.assembly)?
            .method(selected.token)?
            .flags
            & 0x400
            != 0
        {
            return Err("Re-abstracted interface slot has no concrete implementation".into());
        }
        Ok(selected)
    }

    fn slot(&mut self, method: Definition) -> Result<Definition, String> {
        if let Some(slot) = self.slots.get(&method) {
            return Ok(*slot);
        }
        self.charge(1)?;
        let image = self.resolver.assembly(method.assembly)?;
        let flags = image.method(method.token)?.flags;
        let signature = self.signature(method)?;
        if flags & 0x50 != 0x40 || !signature.calling_convention.has_this() {
            return Err("Virtual slot must be an instance virtual method".into());
        }
        let owner = lower::owner_type(self.resolver, method)?;
        let class = lower::class_type(self.resolver, owner)?;
        if class.is_interface() {
            if flags & 0x120 == 0x20
                && self
                    .mappings(owner)?
                    .iter()
                    .any(|&(_, body)| body == method)
            {
                if flags & 0x400 != 0 {
                    return Err(
                        "Abstract interface implementation body cannot be invoked directly".into(),
                    );
                }
                self.slots.insert(method, method);
                return Ok(method);
            }
            if flags & 0x120 != 0x100 {
                return Err("Interface dispatch slot must be nonfinal and newslot".into());
            }
            self.slots.insert(method, method);
            return Ok(method);
        }
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
        if let Some(instance) = self.resolver.instance(method)? {
            return self
                .implementation(instance.method, class)?
                .map(|target| self.resolver.specialize(target, instance.arguments))
                .transpose();
        }
        if self.active.len() >= 64 || !self.active.insert((method, class)) {
            return Err("Virtual/MethodImpl binding cycle or depth exceeds64".into());
        }
        let result = self.implementation_inner(method, class);
        self.active.remove(&(method, class));
        result
    }

    fn implementation_inner(
        &mut self,
        method: Definition,
        class: ClassType,
    ) -> Result<Option<Definition>, String> {
        self.charge(1)?;
        let owner = lower::owner_type(self.resolver, method)?;
        if lower::class_type(self.resolver, owner)?.is_interface() {
            return self.interface_implementation(method, class, owner);
        }
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
            for (declaration, body) in self.mappings(owner)? {
                if lower::class_type(
                    self.resolver,
                    lower::owner_type(self.resolver, declaration)?,
                )?
                .is_interface()
                {
                    continue;
                }
                if self.slot(declaration)? == slot {
                    if self.slot(body)? == slot {
                        return Ok(Some(body));
                    }
                    return self.implementation(body, class);
                }
            }
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
        if let Some(instance) = self.resolver.instance(method)? {
            let signature = self.signature(instance.method)?;
            return Ok(crate::generics::method_types(
                self.resolver,
                instance.method,
                &signature,
                &instance.arguments,
            )?
            .signature);
        }
        let signature = self.signature(method)?;
        lower::bound_signature(
            self.resolver,
            method.assembly,
            &lower::storage_signature(self.resolver, method, &signature)?,
        )
    }

    pub(crate) fn validate_interfaces(&mut self, class: ClassType) -> Result<(), String> {
        let Some(id) = class.identity() else {
            return Ok(());
        };
        let layout = crate::layout::class(self.resolver, id)?;
        self.charge(layout.interfaces.len())?;
        for &interface in &layout.interfaces {
            let mappings = self.mappings(interface)?;
            for method in self.methods(interface)? {
                let flags = self
                    .resolver
                    .assembly(method.assembly)?
                    .method(method.token)?
                    .flags;
                if flags & 0x50 == 0x50 {
                    return Err("Static virtual interface contracts are unsupported".into());
                }
                if flags & 0x50 != 0x40 {
                    continue;
                }
                if mappings.iter().any(|&(_, body)| body == method) {
                    continue;
                }
                self.implementation(method, class)?
                    .ok_or("Allocated class has no interface method implementation")?;
            }
        }
        Ok(())
    }

    pub(crate) fn function(
        &mut self,
        method: Definition,
        classes: &[ClassType],
    ) -> Result<Function, String> {
        self.slot(
            self.resolver
                .instance(method)?
                .map_or(method, |instance| instance.method),
        )?;
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
