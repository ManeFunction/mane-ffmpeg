//! Evaluates simple arithmetic typed into number fields: + − × ÷ and parentheses, e.g. "0-700" or "2*(40+1.5)".
//! Accepts "." or "," as the decimal separator and the typographic minus, times and division signs.

pub fn try_evaluate(text: &str) -> Option<f64> {
    if text.trim().is_empty() {
        return None;
    }
    let chars: Vec<char> = text.chars().collect();
    let mut p = Parser { text: &chars, pos: 0 };
    let value = p.expression()?;
    (p.at_end() && value.is_finite()).then_some(value)
}

struct Parser<'a> {
    text: &'a [char],
    pos: usize,
}

impl Parser<'_> {
    fn at_end(&mut self) -> bool {
        self.skip_spaces();
        self.pos == self.text.len()
    }

    // expression := term (('+' | '-') term)*
    fn expression(&mut self) -> Option<f64> {
        let mut value = self.term()?;
        loop {
            if self.accept(&['+']) {
                value += self.term()?;
            } else if self.accept(&['-', '−', '–']) {
                value -= self.term()?;
            } else {
                return Some(value);
            }
        }
    }

    // term := factor (('*' | '/') factor)*
    fn term(&mut self) -> Option<f64> {
        let mut value = self.factor()?;
        loop {
            if self.accept(&['*', '×', '·']) {
                value *= self.factor()?;
            } else if self.accept(&['/', '÷']) {
                value /= self.factor()?;
            } else {
                return Some(value);
            }
        }
    }

    // factor := ('+' | '-') factor | '(' expression ')' | number
    fn factor(&mut self) -> Option<f64> {
        if self.accept(&['+']) {
            return self.factor();
        }
        if self.accept(&['-', '−', '–']) {
            return self.factor().map(|v| -v);
        }
        if self.accept(&['(']) {
            let value = self.expression()?;
            if !self.accept(&[')']) {
                return None;
            }
            return Some(value);
        }
        self.number()
    }

    fn number(&mut self) -> Option<f64> {
        self.skip_spaces();
        let start = self.pos;
        while self.pos < self.text.len() && (self.text[self.pos].is_ascii_digit() || matches!(self.text[self.pos], '.' | ',')) {
            self.pos += 1;
        }
        let number: String = self.text[start..self.pos].iter().map(|c| if *c == ',' { '.' } else { *c }).collect();
        // .NET's AllowDecimalPoint: digits with at most one point, at least one digit.
        if number.is_empty() || number == "." || number.matches('.').count() > 1 {
            return None;
        }
        let normalized = if number.starts_with('.') { format!("0{number}") } else { number };
        normalized.trim_end_matches('.').parse().ok().or_else(|| normalized.parse().ok())
    }

    fn accept(&mut self, symbols: &[char]) -> bool {
        self.skip_spaces();
        if self.pos >= self.text.len() || !symbols.contains(&self.text[self.pos]) {
            return false;
        }
        self.pos += 1;
        true
    }

    fn skip_spaces(&mut self) {
        while self.pos < self.text.len() && self.text[self.pos].is_whitespace() {
            self.pos += 1;
        }
    }
}
