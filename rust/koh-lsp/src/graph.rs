use crate::paths::key;
use std::collections::{BTreeMap, BTreeSet, VecDeque};

#[derive(Default)]
pub struct Graph {
    edges: BTreeMap<String, BTreeSet<String>>,
    reverse: BTreeMap<String, BTreeSet<String>>,
    names: BTreeMap<String, String>,
}

pub struct Candidate {
    pub path: String,
    pub score: u8,
}

pub struct Discovery {
    pub candidates: Vec<Candidate>,
    pub owners: BTreeMap<String, String>,
}

impl Graph {
    pub fn upsert(&mut self, path: &str, includes: &[String]) {
        let id = key(path);
        self.names.entry(id.clone()).or_insert_with(|| path.into());
        if let Some(old) = self.edges.remove(&id) {
            for target in old {
                if let Some(incoming) = self.reverse.get_mut(&target) {
                    incoming.remove(&id);
                }
            }
        }
        let mut edges = BTreeSet::new();
        for include in includes {
            let target = key(include);
            self.names
                .entry(target.clone())
                .or_insert_with(|| include.clone());
            self.reverse
                .entry(target.clone())
                .or_default()
                .insert(id.clone());
            edges.insert(target);
        }
        self.edges.insert(id, edges);
    }

    pub fn remove(&mut self, path: &str) {
        let id = key(path);
        self.upsert(path, &[]);
        self.edges.remove(&id);
        if let Some(incoming) = self.reverse.remove(&id) {
            for parent in incoming {
                if let Some(edges) = self.edges.get_mut(&parent) {
                    edges.remove(&id);
                }
            }
        }
        self.names.remove(&id);
    }

    pub fn scanned(&self, path: &str) -> bool {
        self.edges.contains_key(&key(path))
    }

    pub fn includes(&self, path: &str) -> BTreeSet<String> {
        self.edges
            .get(&key(path))
            .into_iter()
            .flatten()
            .map(|id| self.names[id].clone())
            .collect()
    }

    pub fn includers(&self, path: &str) -> BTreeSet<String> {
        self.reverse
            .get(&key(path))
            .into_iter()
            .flatten()
            .map(|id| self.names[id].clone())
            .collect()
    }

    pub fn distances(&self, path: &str) -> BTreeMap<String, usize> {
        let mut distances = BTreeMap::from([(key(path), 0)]);
        let mut queue = VecDeque::from([key(path)]);
        while let Some(current) = queue.pop_front() {
            let distance = distances[&current];
            for target in self.edges.get(&current).into_iter().flatten() {
                if !distances.contains_key(target) {
                    distances.insert(target.clone(), distance + 1);
                    queue.push_back(target.clone());
                }
            }
        }
        distances
    }

    pub fn reachable(&self, path: &str) -> BTreeSet<String> {
        self.distances(path)
            .into_keys()
            .map(|id| self.names.get(&id).cloned().unwrap_or_else(|| path.into()))
            .collect()
    }

    pub fn reachable_entrypoints(&self, path: &str, roots: &BTreeSet<String>) -> BTreeSet<String> {
        roots
            .iter()
            .filter(|root| self.distances(root).contains_key(&key(path)))
            .cloned()
            .collect()
    }

    pub fn discover(&self, files: &BTreeSet<String>, open: &BTreeSet<String>) -> Discovery {
        let open: BTreeSet<_> = open.iter().map(|path| key(path)).collect();
        let mut candidates = Vec::new();
        for file in files {
            let asm = key(file).ends_with(".asm");
            let incoming = !self.includers(file).is_empty();
            let outgoing = !self.includes(file).is_empty();
            let score = if asm && outgoing && !incoming {
                4
            } else if asm && open.contains(&key(file)) && !incoming {
                3
            } else if asm && !outgoing && !incoming {
                2
            } else if !asm && open.contains(&key(file)) && !incoming {
                1
            } else {
                0
            };
            if score > 0 {
                candidates.push(Candidate {
                    path: file.clone(),
                    score,
                });
            }
        }
        candidates.sort_by(|a, b| {
            b.score
                .cmp(&a.score)
                .then_with(|| key(&a.path).cmp(&key(&b.path)))
        });
        let mut best = BTreeMap::new();
        let mut owners = BTreeMap::new();
        for candidate in &candidates {
            for (file, distance) in self.distances(&candidate.path) {
                let rank = (
                    std::cmp::Reverse(candidate.score),
                    distance,
                    key(&candidate.path),
                );
                if best.get(&file).is_none_or(|previous| &rank < previous) {
                    best.insert(file.clone(), rank);
                    owners.insert(file, candidate.path.clone());
                }
            }
        }
        Discovery { candidates, owners }
    }
}
