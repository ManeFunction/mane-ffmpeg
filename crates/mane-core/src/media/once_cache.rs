//! A keyed cache whose values are computed once, on the first thread that asks; others asking meanwhile wait for
//! that result instead of computing it again.

use std::collections::HashMap;
use std::hash::Hash;
use std::sync::{Arc, Mutex, OnceLock};

pub struct OnceCache<K, V> {
    map: Mutex<HashMap<K, Arc<OnceLock<V>>>>,
}

impl<K: Eq + Hash + Clone, V: Clone> Default for OnceCache<K, V> {
    fn default() -> Self {
        OnceCache { map: Mutex::new(HashMap::new()) }
    }
}

impl<K: Eq + Hash + Clone, V: Clone> OnceCache<K, V> {
    /// The value if it has been computed.
    pub fn try_get(&self, key: &K) -> Option<V> {
        self.map.lock().unwrap().get(key).and_then(|c| c.get().cloned())
    }

    /// The value, computing it on this thread when nobody has yet (blocks while another thread computes it).
    pub fn get(&self, key: &K, compute: impl FnOnce() -> V) -> V {
        let cell = self.map.lock().unwrap().entry(key.clone()).or_default().clone();
        cell.get_or_init(compute).clone()
    }

    pub fn remove(&self, key: &K) {
        self.map.lock().unwrap().remove(key);
    }

    pub fn clear(&self) {
        self.map.lock().unwrap().clear();
    }
}
