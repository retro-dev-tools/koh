use clrmeta::{Metadata, TableId};
use std::collections::HashMap;

#[derive(Default)]
pub(super) struct Index {
    pub types: HashMap<u32, HashMap<String, HashMap<String, u32>>>,
    pub exports: HashMap<String, HashMap<String, Vec<u32>>>,
    pub methods: HashMap<u32, HashMap<String, Vec<u32>>>,
    pub fields: HashMap<u32, HashMap<String, Vec<u32>>>,
    pub generic_arity: HashMap<(TableId, u32), usize>,
}

fn name(metadata: &Metadata, index: u32, budget: &mut usize) -> Result<String, String> {
    let value = metadata.strings.get(index).map_err(|e| e.to_string())?;
    *budget = budget
        .checked_add(value.len())
        .ok_or("Metadata name size overflow")?;
    if *budget > 64 * 1024 * 1024 {
        return Err("Metadata index names exceed 64 MiB".into());
    }
    Ok(value.to_owned())
}

fn validate_parents(parents: &[u32]) -> Result<(), String> {
    let mut colors = vec![0u8; parents.len()];
    for start in 0..parents.len() {
        let mut current = start;
        let mut path = Vec::new();
        while colors[current] != 2 {
            if colors[current] == 1 {
                return Err("Nested type cycle".into());
            }
            colors[current] = 1;
            path.push(current);
            if parents[current] == 0 {
                break;
            }
            current = parents[current] as usize - 1;
            if current >= parents.len() {
                return Err("Invalid nested type parent".into());
            }
        }
        for index in path {
            colors[index] = 2;
        }
    }
    Ok(())
}

impl Index {
    pub fn new(metadata: &Metadata, budget: &mut usize) -> Result<Self, String> {
        let mut result = Self::default();
        let mut parents = vec![0u32; metadata.type_defs.len()];
        for nested in &metadata.nested_classes {
            let index = nested
                .nested_class
                .checked_sub(1)
                .and_then(|row| parents.get_mut(row as usize))
                .ok_or("Invalid nested type row")?;
            if *index != 0
                || nested.enclosing_class == 0
                || nested.enclosing_class as usize > metadata.type_defs.len()
            {
                return Err("Invalid or duplicate nested type owner".into());
            }
            *index = nested.enclosing_class;
        }
        validate_parents(&parents)?;
        for (index, ty) in metadata.type_defs.iter().enumerate() {
            let namespace = name(metadata, ty.type_namespace, budget)?;
            let type_name = name(metadata, ty.type_name, budget)?;
            if result
                .types
                .entry(parents[index])
                .or_default()
                .entry(namespace)
                .or_default()
                .insert(type_name, 0x02000001 + index as u32)
                .is_some()
            {
                return Err("Duplicate type identity".into());
            }
            let type_row = index as u32 + 1;
            for (row, method) in metadata.get_type_methods(type_row) {
                result
                    .methods
                    .entry(type_row)
                    .or_default()
                    .entry(name(metadata, method.name, budget)?)
                    .or_default()
                    .push(0x06000000 | row);
            }
            for (row, field) in metadata.get_type_fields(type_row) {
                result
                    .fields
                    .entry(type_row)
                    .or_default()
                    .entry(name(metadata, field.name, budget)?)
                    .or_default()
                    .push(0x04000000 | row);
            }
        }
        for (index, export) in metadata.exported_types.iter().enumerate() {
            if export.implementation.table != Some(TableId::AssemblyRef) {
                continue;
            }
            result
                .exports
                .entry(name(metadata, export.type_namespace, budget)?)
                .or_default()
                .entry(name(metadata, export.type_name, budget)?)
                .or_default()
                .push(0x27000001 + index as u32);
        }
        let mut generic_numbers = HashMap::<_, Vec<_>>::new();
        for parameter in &metadata.generic_params {
            let table = parameter
                .owner
                .table
                .ok_or("Invalid generic parameter owner")?;
            let count = match table {
                TableId::TypeDef => metadata.type_defs.len(),
                TableId::MethodDef => metadata.method_defs.len(),
                _ => return Err("Invalid generic parameter owner table".into()),
            };
            if parameter.owner.row == 0 || parameter.owner.row as usize > count {
                return Err("Invalid generic parameter owner row".into());
            }
            generic_numbers
                .entry((table, parameter.owner.row))
                .or_default()
                .push(parameter.number);
        }
        for (owner, mut numbers) in generic_numbers {
            numbers.sort_unstable();
            if numbers
                .iter()
                .enumerate()
                .any(|(index, &number)| index != usize::from(number))
            {
                return Err("Duplicate or noncontiguous generic parameter indices".into());
            }
            result.generic_arity.insert(owner, numbers.len());
        }
        Ok(result)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rstest::rstest;

    #[rstest]
    #[case(&[], true)]
    #[case(&[0, 1, 2], true)]
    #[case(&[2, 3, 0], true)]
    #[case(&[1], false)]
    #[case(&[2, 1], false)]
    #[case(&[0, 3, 2], false)]
    #[case(&[4], false)]
    fn nested_graph_validation(#[case] parents: &[u32], #[case] valid: bool) {
        assert_eq!(validate_parents(parents).is_ok(), valid);
    }
}
