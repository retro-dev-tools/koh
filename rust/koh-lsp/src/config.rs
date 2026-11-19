use crate::paths::{key, normalize};
use serde_json::Value;
use std::{
    collections::BTreeSet,
    io,
    path::{Path, PathBuf},
};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Project {
    pub name: String,
    pub entrypoint: PathBuf,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Configuration {
    Heuristic,
    Configured(Vec<Project>),
    Invalid(Vec<String>),
}

impl Configuration {
    pub fn load(folder: &Path) -> Self {
        match std::fs::read_to_string(folder.join("koh.yaml")) {
            Ok(text) => Self::parse(&text, folder),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Self::Heuristic,
            Err(error) => Self::Invalid(vec![format!("Failed to read koh.yaml: {error}")]),
        }
    }

    pub fn parse(text: &str, folder: &Path) -> Self {
        let yaml: Value = match serde_yaml_ng::from_str(text) {
            Ok(value) => value,
            Err(error) => return Self::Invalid(vec![format!("Invalid YAML syntax: {error}")]),
        };
        if yaml.is_null() {
            return Self::Invalid(vec!["Config file is empty.".into()]);
        }
        let mut errors = Vec::new();
        match yaml.get("version") {
            None | Some(Value::Null) => errors.push("Missing required field: 'version'.".into()),
            Some(version) if version != 1 => errors.push(format!(
                "Unsupported config version: {version}. Only version 1 is supported."
            )),
            _ => {}
        }
        let entries = match yaml.get("projects") {
            None | Some(Value::Null) => {
                errors.push("Missing required field: 'projects'.".into());
                None
            }
            Some(Value::Array(entries)) if entries.is_empty() => {
                errors.push("'projects' must not be empty.".into());
                None
            }
            Some(Value::Array(entries)) => Some(entries),
            _ => {
                errors.push("'projects' must be a list.".into());
                None
            }
        };
        let mut projects = Vec::new();
        let mut names = BTreeSet::new();
        let mut paths = BTreeSet::new();
        for (index, entry) in entries.into_iter().flatten().enumerate() {
            let mut fields = Vec::new();
            for field in ["name", "entrypoint"] {
                let value = entry[field].as_str().filter(|s| !s.trim().is_empty());
                if value.is_none() {
                    errors.push(format!(
                        "projects[{index}]: Missing required field '{field}'."
                    ));
                }
                fields.push(value);
            }
            let (Some(name), Some(path)) = (fields[0], fields[1]) else {
                continue;
            };
            if Path::new(path).has_root() {
                errors.push(format!("Entrypoint must be a relative path: '{path}'."));
                continue;
            }
            let entrypoint =
                match normalize(&folder.join(path.replace('/', std::path::MAIN_SEPARATOR_STR))) {
                    Ok(path) => path,
                    Err(error) => {
                        errors.push(format!("Invalid entrypoint: {error}"));
                        continue;
                    }
                };
            if !names.insert(key(name)) {
                errors.push(format!("Duplicate project name: '{name}'."));
            }
            if !paths.insert(key(&entrypoint.to_string_lossy())) {
                errors.push(format!(
                    "Duplicate entrypoint (after normalization): '{path}'."
                ));
            }
            projects.push(Project {
                name: name.into(),
                entrypoint,
            });
        }
        if errors.is_empty() {
            Self::Configured(projects)
        } else {
            Self::Invalid(errors)
        }
    }
}
