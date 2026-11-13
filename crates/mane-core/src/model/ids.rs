//! Short random ids for sources, clips and presets.

/// 12 lowercase hex characters, like the first part of a GUID in "N" format.
pub fn new() -> String {
    use rand::Rng;
    let mut rng = rand::rng();
    (0..12).map(|_| std::char::from_digit(rng.random_range(0..16), 16).unwrap()).collect()
}
