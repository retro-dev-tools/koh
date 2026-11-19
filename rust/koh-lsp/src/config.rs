use crate::paths::{key, normalize};
use serde::Deserialize;
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

#[derive(Deserialize)]
struct ConfigYaml {
    version: Option<String>,
    projects: Option<Vec<ProjectYaml>>,
}

#[derive(Deserialize)]
struct ProjectYaml {
    name: Option<String>,
    entrypoint: Option<String>,
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
        let yaml: ConfigYaml = match serde_yaml_ng::from_str(text) {
            Ok(Some(value)) => value,
            Ok(None) => return Self::Invalid(vec!["Config file is empty.".into()]),
            Err(error) => return Self::Invalid(vec![format!("Invalid YAML syntax: {error}")]),
        };
        let mut errors = Vec::new();
        match yaml.version.as_deref() {
            None => errors.push("Missing required field: 'version'.".into()),
            Some(version) if version.trim().parse::<i32>() != Ok(1) => errors.push(format!(
                "Unsupported config version: {version}. Only version 1 is supported."
            )),
            _ => {}
        }
        let entries = match yaml.projects {
            None => {
                errors.push("Missing required field: 'projects'.".into());
                None
            }
            Some(entries) if entries.is_empty() => {
                errors.push("'projects' must not be empty.".into());
                None
            }
            Some(entries) => Some(entries),
        };
        let mut projects = Vec::new();
        let mut names = BTreeSet::new();
        let mut paths = BTreeSet::new();
        for (index, entry) in entries.into_iter().flatten().enumerate() {
            let mut fields = Vec::new();
            for (field, value) in [
                ("name", entry.name.as_deref()),
                ("entrypoint", entry.entrypoint.as_deref()),
            ] {
                let value = value.filter(|s| !s.trim().is_empty());
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
            if Path::new(path).has_root()
                || matches!(
                    Path::new(path).components().next(),
                    Some(std::path::Component::Prefix(_))
                )
            {
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
