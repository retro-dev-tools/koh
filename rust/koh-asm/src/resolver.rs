use serde::Deserialize;
use std::{
    cell::RefCell,
    collections::HashMap,
    path::{Path, PathBuf},
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Resolution {
    pub including_file: String,
    pub requested: String,
    pub resolved: String,
}

#[derive(Deserialize)]
pub struct SearchRoots {
    pub file: String,
    pub roots: Vec<String>,
}

#[derive(Clone, Default)]
pub struct ResolverPolicy {
    resolutions: HashMap<(String, String), String>,
    roots: HashMap<String, Vec<String>>,
    inherited: RefCell<HashMap<(String, String), Vec<String>>>,
}

impl ResolverPolicy {
    pub fn new(
        resolutions: Vec<Resolution>,
        search_roots: Vec<SearchRoots>,
    ) -> Result<Self, String> {
        let mut policy = Self::default();
        for resolution in resolutions {
            if resolution.including_file.is_empty()
                || resolution.requested.is_empty()
                || resolution.resolved.is_empty()
            {
                return Err("file resolver mappings must have nonempty paths".into());
            }
            let key = (
                crate::assembler::overlay_key(&resolution.including_file),
                resolution.requested,
            );
            if policy
                .resolutions
                .insert(key, resolution.resolved.clone())
                .is_some_and(|old| old != resolution.resolved)
            {
                return Err("conflicting file resolver mappings".into());
            }
        }
        for roots in search_roots {
            if roots.file.is_empty() || roots.roots.iter().any(String::is_empty) {
                return Err("file resolver roots must have nonempty paths".into());
            }
            let key = crate::assembler::overlay_key(&roots.file);
            if policy
                .roots
                .insert(key, roots.roots.clone())
                .is_some_and(|old| old != roots.roots)
            {
                return Err("conflicting file resolver roots".into());
            }
        }
        Ok(policy)
    }

    fn roots_for(&self, current: &str, owner: &str) -> Vec<String> {
        let key = crate::assembler::overlay_key(current);
        self.roots
            .get(&key)
            .cloned()
            .or_else(|| {
                self.inherited
                    .borrow()
                    .get(&(crate::assembler::overlay_key(owner), key))
                    .cloned()
            })
            .or_else(|| {
                self.roots
                    .get(&crate::assembler::overlay_key(owner))
                    .cloned()
            })
            .unwrap_or_default()
    }

    pub fn candidates(&self, current: &str, name: &str, owner: &str) -> Vec<PathBuf> {
        if let Some(path) = self
            .resolutions
            .get(&(crate::assembler::overlay_key(current), name.into()))
        {
            return vec![crate::assembler::source_path(path)];
        }
        let relative = Path::new(name);
        if relative.is_absolute() || name.starts_with("file://") {
            return vec![crate::assembler::source_path(name)];
        }
        let roots = self.roots_for(current, owner);
        let mut candidates: Vec<_> = if roots.is_empty() {
            vec![crate::assembler::source_path(name)]
        } else {
            roots
                .iter()
                .map(|root| crate::assembler::source_path(root).join(relative))
                .collect()
        };
        candidates.push(
            crate::assembler::source_path(current)
                .parent()
                .unwrap_or(Path::new("."))
                .join(relative),
        );
        candidates
    }

    pub fn inherit(&self, current: &str, resolved: &str, owner: &str) {
        let roots = self.roots_for(current, owner);
        if !roots.is_empty() {
            self.inherited.borrow_mut().insert(
                (
                    crate::assembler::overlay_key(owner),
                    crate::assembler::overlay_key(resolved),
                ),
                roots,
            );
        }
    }

    pub fn readfile_candidates(&self, current: &str, name: &str, owner: &str) -> Vec<PathBuf> {
        if self
            .resolutions
            .contains_key(&(crate::assembler::overlay_key(current), name.into()))
            || !self.roots_for(current, owner).is_empty()
            || Path::new(name).is_absolute()
            || name.starts_with("file://")
        {
            return self.candidates(current, name, owner);
        }
        vec![
            crate::assembler::source_path(current)
                .parent()
                .unwrap_or(Path::new("."))
                .join(name),
        ]
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn inherited_roots_are_isolated_by_owner() {
        let policy = ResolverPolicy::new(
            Vec::new(),
            vec![
                SearchRoots {
                    file: "/entry/a.asm".into(),
                    roots: vec!["/a".into()],
                },
                SearchRoots {
                    file: "/entry/b.asm".into(),
                    roots: vec!["/b".into()],
                },
            ],
        )
        .unwrap();
        policy.inherit("/entry/a.asm", "/shared.inc", "/entry/a.asm");
        assert_eq!(
            policy.candidates("/shared.inc", "leaf.inc", "/entry/b.asm")[0],
            crate::assembler::source_path("/b").join("leaf.inc")
        );
        assert_eq!(
            policy.candidates("/shared.inc", "leaf.inc", "/entry/a.asm")[0],
            crate::assembler::source_path("/a").join("leaf.inc")
        );
        policy.inherit("/entry/b.asm", "/shared.inc", "/entry/b.asm");
        assert_eq!(
            policy.candidates("/shared.inc", "leaf.inc", "/entry/b.asm")[0],
            crate::assembler::source_path("/b").join("leaf.inc")
        );
    }
}
