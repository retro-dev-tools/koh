use super::{Definition, Resolver, coded, framework_key, framework_type, identity, row};
use clrmeta::{ElementType, MethodSig, TableId, TypeSig};

#[derive(Debug, Clone)]
pub struct DelegateDefinition {
    pub identity: Definition,
    pub constructor: Definition,
    pub invoke: Definition,
    pub signature: MethodSig,
    pub type_arguments: Vec<TypeSig>,
    pub argument_assembly: usize,
}

impl Resolver<'_> {
    pub(crate) fn charge_delegate_work(&self, amount: usize) -> Result<(), String> {
        let work = self
            .delegate_type_work
            .get()
            .checked_add(amount)
            .filter(|&work| work <= 1_048_576)
            .ok_or("Delegate binding work exceeds1048576")?;
        self.delegate_type_work.set(work);
        Ok(())
    }
    pub fn delegate_type(
        &self,
        assembly: usize,
        token: u32,
    ) -> Result<Option<DelegateDefinition>, String> {
        let (owner, type_arguments) = self.parent(assembly, token)?;
        self.delegate_definition(assembly, owner, type_arguments)
    }

    pub(crate) fn delegate_signature_type(
        &self,
        assembly: usize,
        ty: &TypeSig,
    ) -> Result<Option<DelegateDefinition>, String> {
        let (encoded, arguments) = match ty {
            TypeSig::Class(encoded) => (*encoded, Vec::new()),
            TypeSig::GenericInst {
                is_value_type: false,
                type_ref,
                type_args,
            } => (*type_ref, type_args.clone()),
            _ => return Ok(None),
        };
        if encoded & 3 == 3 {
            self.signature_type(assembly, encoded)?;
            return Ok(None);
        }
        let owner = self.resolve_type(assembly, super::type_token(encoded)?)?;
        self.delegate_definition(assembly, owner, arguments)
    }

    fn delegate_definition(
        &self,
        assembly: usize,
        owner: Definition,
        type_arguments: Vec<TypeSig>,
    ) -> Result<Option<DelegateDefinition>, String> {
        let image = self.assembly(owner.assembly)?;
        let metadata = image.metadata();
        let ty = row(&metadata.type_defs, owner.token, 2)?;
        if ty.extends.row == 0 {
            return Ok(None);
        }
        let parent = match ty.extends.table {
            Some(TableId::TypeRef) => framework_type(metadata, coded(ty.extends)?)?,
            Some(TableId::TypeDef) => {
                let definition = row(&metadata.type_defs, coded(ty.extends)?, 2)?;
                let name = image.name()?;
                let own = identity(metadata)?;
                if framework_key(name).is_some_and(|expected| own.key == Some(expected))
                    && own.culture.is_empty()
                    && metadata.assemblies[0].flags & 0x0f00 == 0
                {
                    Some((
                        metadata
                            .strings
                            .get(definition.type_namespace)
                            .map_err(|e| e.to_string())?,
                        metadata
                            .strings
                            .get(definition.type_name)
                            .map_err(|e| e.to_string())?,
                    ))
                } else {
                    None
                }
            }
            _ => None,
        };
        if !matches!(parent, Some(("System", "Delegate" | "MulticastDelegate"))) {
            return Ok(None);
        }
        if ty.flags & 0x118 != 0x100 || ty.flags & 0xa0 != 0 {
            return Err("Delegate definition must be a sealed AutoLayout class".into());
        }
        self.check_arity(owner, type_arguments.len())?;
        if !type_arguments.is_empty() {
            self.charge_delegate_work(metadata.generic_params.len())?;
            for parameter in metadata.generic_params.iter().filter(|parameter| {
                parameter.owner.table == Some(TableId::TypeDef)
                    && parameter.owner.row == owner.token & 0xffffff
            }) {
                if parameter.flags & !0x3f != 0
                    || parameter.flags & 3 == 3
                    || parameter.flags & 0x0c == 0x0c
                {
                    return Err(format!(
                        "Invalid delegate generic parameter flags for !{}",
                        parameter.number
                    ));
                }
            }
        }
        for argument in &type_arguments {
            self.validate_type(assembly, argument, 0)?;
        }
        let field_end = metadata
            .type_defs
            .get((owner.token & 0xffffff) as usize)
            .map(|ty| ty.field_list)
            .unwrap_or(metadata.fields.len() as u32 + 1);
        let field_start = ty
            .field_list
            .checked_sub(1)
            .ok_or("Invalid delegate field range")? as usize;
        let field_end = field_end
            .checked_sub(1)
            .ok_or("Invalid delegate field range")? as usize;
        let fields = metadata
            .fields
            .get(field_start..field_end)
            .ok_or("Invalid delegate field range")?;
        let end = metadata
            .type_defs
            .get((owner.token & 0xffffff) as usize)
            .map(|ty| ty.method_list)
            .unwrap_or(metadata.method_defs.len() as u32 + 1);
        let methods = end
            .checked_sub(ty.method_list)
            .ok_or("Invalid delegate method range")?;
        self.charge_delegate_work(fields.len() + methods as usize + type_arguments.len() + 1)?;
        if fields.iter().any(|field| field.flags & 0x10 == 0) {
            return Err("Delegate instance fields require a runtime storage contract".into());
        }
        let mut constructor = None;
        let mut invoke = None;
        for rid in ty.method_list..end {
            let token = 0x06000000 | rid;
            let method = image.method(token)?;
            let name = metadata
                .strings
                .get(method.name)
                .map_err(|e| e.to_string())?;
            if !matches!(name, ".ctor" | "Invoke") {
                continue;
            }
            let signature = image.method_signature(token)?;
            if signature.calling_convention.0 != 0x20
                || signature.generic_param_count != 0
                || signature.sentinel.is_some()
                || method.flags & 0x2410 != 0
                || method.flags & 7 != 6
                || method.impl_flags & 7 != 3
                || method.rva != 0
            {
                return Err(format!("Invalid delegate {name} runtime method contract"));
            }
            let definition = Definition {
                assembly: owner.assembly,
                token,
            };
            if name == ".ctor" {
                if constructor.replace(definition).is_some()
                    || signature.return_type != TypeSig::Primitive(ElementType::Void)
                    || signature.params
                        != [
                            TypeSig::Primitive(ElementType::Object),
                            TypeSig::Primitive(ElementType::IntPtr),
                        ]
                    || method.flags & 0x1800 != 0x1800
                {
                    return Err("Invalid delegate constructor signature".into());
                }
            } else if invoke.replace((definition, signature)).is_some() || method.flags & 0x40 == 0
            {
                return Err("Delegate requires one virtual Invoke method".into());
            }
        }
        let (invoke, signature) = invoke.ok_or("Delegate has no Invoke method")?;
        Ok(Some(DelegateDefinition {
            identity: owner,
            constructor: constructor.ok_or("Delegate has no runtime constructor")?,
            invoke,
            signature,
            type_arguments,
            argument_assembly: assembly,
        }))
    }
}
