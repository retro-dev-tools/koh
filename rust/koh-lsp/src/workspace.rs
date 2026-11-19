use crate::{
    config::Configuration,
    graph::Graph,
    paths::{Lines, file_key, key, normalize, same_file},
    worker::Worker,
};
use serde_json::{Value, json};
use std::{
    collections::{BTreeMap, BTreeSet, HashMap, VecDeque},
    io,
    path::Path,
    rc::Rc,
};

pub struct Document {
    pub path: String,
    pub text: String,
    pub lines: Lines,
    pub syntax: Value,
    pub diagnostics: Vec<Value>,
}

#[derive(Clone)]
pub struct Project {
    pub name: String,
    pub entrypoint: String,
    pub analysis: Rc<Value>,
    files: BTreeSet<String>,
}

impl Project {
    pub fn contains(&self, path: &Path) -> bool {
        normalize(path).is_ok_and(|path| self.files.contains(&key(&path.to_string_lossy())))
    }
}

#[derive(Default)]
pub struct Workspace {
    documents: BTreeMap<String, Document>,
    projects: Vec<Project>,
    owners: BTreeMap<String, String>,
    folder: Option<std::path::PathBuf>,
    pub configuration: Option<Configuration>,
    worker: Option<Worker>,
}

impl Workspace {
    pub(crate) fn request(&mut self, request: &Value) -> io::Result<Value> {
        if self.worker.is_none() {
            self.worker = Some(Worker::assembler()?);
        }
        let result = self.worker.as_mut().unwrap().request(request);
        if result.is_err() {
            self.worker = None;
        }
        result
    }

    pub fn initialize(&mut self, folder: &Path) -> io::Result<()> {
        let folder = normalize(folder)?;
        self.configuration = Some(Configuration::load(&folder));
        self.folder = Some(folder);
        self.rebuild(None)
    }

    pub fn reload(&mut self) -> io::Result<()> {
        if let Some(folder) = self.folder.clone() {
            self.initialize(&folder)?;
        }
        Ok(())
    }

    pub fn open(&mut self, path: &Path, text: &str) -> io::Result<()> {
        let path = normalize(path)?.to_string_lossy().into_owned();
        let parsed = self.request(
            &json!({"version":1,"op":"parse","sources":[{"path":path,"text":text}],"roots":[path]}),
        )?;
        let syntax = parsed["documents"]
            .as_array()
            .and_then(|docs| docs.first())
            .cloned()
            .ok_or_else(|| {
                io::Error::new(
                    io::ErrorKind::InvalidData,
                    "assembler omitted parsed document",
                )
            })?;
        let diagnostics = parsed["diagnostics"].as_array().unwrap().clone();
        self.documents.insert(
            key(&path),
            Document {
                path: path.clone(),
                text: text.into(),
                lines: Lines::new(text),
                syntax,
                diagnostics,
            },
        );
        self.rebuild(Some(&path))
    }

    pub fn close(&mut self, path: &Path) -> io::Result<()> {
        let path = normalize(path)?.to_string_lossy().into_owned();
        self.documents.remove(&key(&path));
        self.rebuild(Some(&path))
    }

    pub fn document(&self, path: &Path) -> Option<&Document> {
        self.documents
            .get(&key(&normalize(path).ok()?.to_string_lossy()))
    }

    pub fn open_documents(&self) -> impl Iterator<Item = &Document> {
        self.documents.values()
    }
    pub fn projects(&self) -> &[Project] {
        &self.projects
    }

    pub fn folder(&self) -> Option<&Path> {
        self.folder.as_deref()
    }

    pub fn project_for(&self, path: &Path) -> Option<&Project> {
        let owner = self
            .owners
            .get(&key(&normalize(path).ok()?.to_string_lossy()))?;
        self.projects
            .iter()
            .find(|project| key(&project.entrypoint) == *owner)
    }

    pub fn text(&self, path: &Path) -> io::Result<String> {
        self.document(path)
            .map_or_else(|| std::fs::read_to_string(path), |doc| Ok(doc.text.clone()))
    }

    pub fn diagnostics(&self, path: &Path) -> Vec<Value> {
        let Some(doc) = self.document(path) else {
            return Vec::new();
        };
        let mut diagnostics = doc.diagnostics.clone();
        if let Some(project) = self.project_for(path) {
            for diagnostic in project.analysis["diagnostics"].as_array().unwrap() {
                if diagnostic["path"]
                    .as_str()
                    .is_some_and(|p| same_file(p, &doc.path))
                    && !diagnostics.contains(diagnostic)
                {
                    diagnostics.push(diagnostic.clone());
                }
            }
        }
        diagnostics
    }

    fn exists(&self, path: &Path) -> bool {
        self.document(path).is_some() || path.is_file()
    }

    fn resolve(&self, current: &str, included: &str, base: &Path) -> io::Result<String> {
        let first = normalize(&base.join(included))?;
        let resolved = if self.exists(&first) {
            first
        } else {
            normalize(&Path::new(current).parent().unwrap_or(base).join(included))?
        };
        Ok(self.document(&resolved).map_or_else(
            || resolved.to_string_lossy().into_owned(),
            |doc| doc.path.clone(),
        ))
    }

    fn analyze(&mut self, roots: &[String]) -> io::Result<Value> {
        let mut sources: Vec<_> = self
            .documents
            .values()
            .map(|doc| json!({"path":doc.path,"text":doc.text}))
            .collect();
        let mut searches = Vec::new();
        for root in roots {
            if !sources.iter().any(|s| s["path"] == *root) {
                let text = match self.text(Path::new(root)) {
                    Ok(text) => text,
                    Err(error) if error.kind() == io::ErrorKind::NotFound => String::new(),
                    Err(error) => return Err(error),
                };
                sources.push(json!({"path":root,"text":text}));
            }
            let base = Path::new(root).parent().unwrap_or(Path::new("."));
            searches.push(json!({"file":root,"roots":[base]}));
        }
        let mut resolutions = Vec::new();
        if roots.len() == 1 {
            let base = Path::new(&roots[0]).parent().unwrap_or(Path::new("."));
            for doc in self.documents.values() {
                for include in doc.syntax["includes"].as_array().into_iter().flatten() {
                    let Some(requested) = include["path"].as_str() else {
                        continue;
                    };
                    let resolved = self.resolve(&doc.path, requested, base)?;
                    resolutions.push(
                        json!({"includingFile":doc.path,"requested":requested,"resolved":resolved}),
                    );
                }
            }
        }
        self.request(&json!({"version":1,"op":"analyze","sources":sources,"roots":roots,"searchRoots":searches,"resolutions":resolutions}))
    }

    fn graph(&self, analysis: &Value, root: &str) -> io::Result<Graph> {
        let mut documents = HashMap::new();
        for doc in analysis["documents"].as_array().unwrap() {
            if let Some(path) = doc["path"].as_str().and_then(file_key) {
                documents.entry(path).or_insert(doc);
            }
        }
        let mut graph = Graph::default();
        let mut pending = VecDeque::from([root.to_string()]);
        let mut visited = BTreeSet::new();
        let base = Path::new(root).parent().unwrap_or(Path::new("."));
        while let Some(path) = pending.pop_front() {
            if !visited.insert(key(&path)) {
                continue;
            }
            let Some(doc) = file_key(&path).and_then(|id| documents.get(&id)) else {
                continue;
            };
            let includes: Vec<_> = doc["includes"]
                .as_array()
                .into_iter()
                .flatten()
                .filter(|include| include["binary"] != true)
                .filter_map(|include| include["path"].as_str())
                .map(|include| self.resolve(&path, include, base))
                .collect::<io::Result<_>>()?;
            pending.extend(includes.iter().cloned());
            graph.upsert(&path, &includes);
        }
        Ok(graph)
    }

    fn rebuild(&mut self, changed: Option<&str>) -> io::Result<()> {
        let mut projects = Vec::new();
        let mut owners = BTreeMap::new();
        match self.configuration.clone() {
            Some(Configuration::Invalid(_)) => {}
            Some(Configuration::Configured(mut definitions)) => {
                definitions.sort_by_key(|def| key(&def.entrypoint.to_string_lossy()));
                for def in definitions {
                    let root = def.entrypoint.to_string_lossy().into_owned();
                    let unchanged = self
                        .projects
                        .iter()
                        .find(|project| {
                            same_file(&project.entrypoint, &root)
                                && changed.is_some_and(|path| !project.contains(Path::new(path)))
                        })
                        .cloned();
                    let project = match unchanged {
                        Some(project) => project,
                        None => {
                            let analysis = self.analyze(std::slice::from_ref(&root))?;
                            let files = reachable_keys(&self.graph(&analysis, &root)?, &root);
                            Project {
                                name: def.name,
                                entrypoint: root.clone(),
                                analysis: Rc::new(analysis),
                                files,
                            }
                        }
                    };
                    for file in &project.files {
                        owners.entry(file.clone()).or_insert_with(|| key(&root));
                    }
                    projects.push(project);
                }
            }
            Some(Configuration::Heuristic) => {
                let open: BTreeSet<_> = self
                    .documents
                    .values()
                    .map(|doc| doc.path.clone())
                    .collect();
                let mut graph = Graph::default();
                let mut files = open.clone();
                let mut analyses = BTreeMap::new();
                for root in &open {
                    let analysis = self.analyze(std::slice::from_ref(root))?;
                    let local = self.graph(&analysis, root)?;
                    for file in local.reachable(root) {
                        let includes = local.includes(&file).into_iter().collect::<Vec<_>>();
                        graph.upsert(&file, &includes);
                        files.insert(file);
                    }
                    analyses.insert(key(root), (analysis, reachable_keys(&local, root)));
                }
                let discovered = graph.discover(&files, &open);
                owners = discovered
                    .owners
                    .into_iter()
                    .map(|(file, owner)| (file, key(&owner)))
                    .collect();
                for candidate in discovered.candidates {
                    let root = candidate.path;
                    let (analysis, files) = match analyses.remove(&key(&root)) {
                        Some(cached) => cached,
                        None => {
                            let analysis = self.analyze(std::slice::from_ref(&root))?;
                            let files = reachable_keys(&self.graph(&analysis, &root)?, &root);
                            (analysis, files)
                        }
                    };
                    let name = Path::new(&root)
                        .file_stem()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .into_owned();
                    projects.push(Project {
                        name,
                        entrypoint: root,
                        analysis: Rc::new(analysis),
                        files,
                    });
                }
            }
            None => {
                let roots: Vec<_> = self
                    .documents
                    .values()
                    .map(|doc| doc.path.clone())
                    .collect();
                if let Some(root) = roots.first() {
                    let analysis = self.analyze(&roots)?;
                    let files = roots.iter().map(|p| key(p)).collect();
                    for file in &roots {
                        owners.insert(key(file), key(root));
                    }
                    projects.push(Project {
                        name: String::new(),
                        entrypoint: root.clone(),
                        analysis: Rc::new(analysis),
                        files,
                    });
                }
            }
        }
        self.projects = projects;
        self.owners = owners;
        Ok(())
    }
}

fn reachable_keys(graph: &Graph, root: &str) -> BTreeSet<String> {
    graph.reachable(root).iter().map(|p| key(p)).collect()
}
