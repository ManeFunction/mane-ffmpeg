mod common;
use common::s;
use mane_core::model::Time;
use mane_core::mpv::seek_pacer::{ECHO_WINDOW, SeekPacer};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Default)]
struct State {
    sent: Vec<Time>,
    mpv_seeking: bool,
    armed: bool,
    now: Option<Instant>,
}

struct Fake {
    state: Arc<Mutex<State>>,
    pacer: SeekPacer,
}

impl Fake {
    fn new() -> Fake {
        let state = Arc::new(Mutex::new(State { now: Some(Instant::now()), ..Default::default() }));
        let (a, b, c, d) = (state.clone(), state.clone(), state.clone(), state.clone());
        let pacer = SeekPacer::new(
            Box::new(move |t| a.lock().unwrap().sent.push(t)),
            Box::new(move || b.lock().unwrap().mpv_seeking),
            Box::new(move |armed| c.lock().unwrap().armed = armed),
            Some(Box::new(move || d.lock().unwrap().now.unwrap())),
        );
        Fake { state, pacer }
    }

    fn sent(&self) -> Vec<Time> {
        self.state.lock().unwrap().sent.clone()
    }

    fn armed(&self) -> bool {
        self.state.lock().unwrap().armed
    }

    fn tick(&self) {
        assert!(self.armed(), "no check was armed");
        self.state.lock().unwrap().armed = false;
        self.pacer.check();
    }

    fn advance(&self, d: Duration) {
        let mut st = self.state.lock().unwrap();
        st.now = Some(st.now.unwrap() + d);
    }
}

#[test]
fn seeks_during_a_seek_collapse_into_the_latest_which_goes_when_it_finishes() {
    let f = Fake::new();
    f.pacer.seek(s(1.0));
    f.pacer.seek(s(2.0));
    f.pacer.seek(s(3.0));
    assert_eq!(f.sent(), [s(1.0)]);
    f.pacer.restarted();
    assert_eq!(f.sent(), [s(1.0), s(3.0)]);
    f.pacer.restarted();
    assert!(!f.armed());
    f.pacer.seek(s(4.0));
    assert_eq!(f.sent(), [s(1.0), s(3.0), s(4.0)]);
}

#[test]
fn coming_back_to_the_time_under_way_costs_no_second_seek() {
    let f = Fake::new();
    f.pacer.seek(s(1.0));
    f.pacer.seek(s(2.0));
    f.pacer.seek(s(1.0));
    f.pacer.restarted();
    assert_eq!(f.sent(), [s(1.0)]);
}

#[test]
fn a_slow_seek_keeps_the_next_waiting_and_a_lost_one_lets_it_go() {
    let f = Fake::new();
    f.pacer.seek(s(1.0));
    f.pacer.seek(s(2.0));
    f.state.lock().unwrap().mpv_seeking = true;
    f.tick();
    f.tick();
    assert_eq!(f.sent(), [s(1.0)]);
    f.state.lock().unwrap().mpv_seeking = false;
    f.tick();
    assert_eq!(f.sent(), [s(1.0), s(2.0)]);
}

#[test]
fn a_reset_drops_the_seeks_and_the_next_goes_at_once() {
    let f = Fake::new();
    f.pacer.seek(s(1.0));
    f.pacer.seek(s(2.0));
    f.pacer.reset();
    f.pacer.restarted();
    assert_eq!(f.sent(), [s(1.0)]);
    f.pacer.seek(s(5.0));
    assert_eq!(f.sent(), [s(1.0), s(5.0)]);
}

#[test]
fn positions_echo_while_a_seek_waits_or_was_just_sent() {
    let f = Fake::new();
    assert!(!f.pacer.may_echo());
    f.pacer.seek(s(1.0));
    assert!(f.pacer.may_echo());
    f.pacer.seek(s(2.0));
    f.advance(ECHO_WINDOW * 2);
    assert!(f.pacer.may_echo());
    f.pacer.restarted();
    assert!(f.pacer.may_echo());
    f.advance(ECHO_WINDOW * 2);
    assert!(!f.pacer.may_echo());
    f.pacer.restarted();
    f.pacer.seek(s(3.0));
    assert!(f.pacer.may_echo());
    f.pacer.forget_last_seek();
    assert!(!f.pacer.may_echo());
}

#[test]
fn nothing_is_sent_or_armed_once_disposed() {
    let f = Fake::new();
    f.pacer.seek(s(1.0));
    f.pacer.seek(s(2.0));
    f.pacer.dispose();
    f.pacer.restarted();
    f.pacer.check();
    assert_eq!(f.sent(), [s(1.0)]);
}

#[test]
fn disposing_waits_for_a_check_that_is_asking_mpv() {
    use std::sync::mpsc::channel;
    let (asking_tx, asking_rx) = channel();
    let (answer_tx, answer_rx) = channel::<()>();
    let answer_rx = Mutex::new(answer_rx);
    let pacer = Arc::new(SeekPacer::new(
        Box::new(|_| {}),
        Box::new(move || {
            let _ = asking_tx.send(());
            let _ = answer_rx.lock().unwrap().recv_timeout(Duration::from_secs(10));
            true
        }),
        Box::new(|_| {}),
        None,
    ));
    pacer.seek(s(1.0));
    let p = pacer.clone();
    let check = std::thread::spawn(move || p.check());
    asking_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    let p = pacer.clone();
    let (done_tx, done_rx) = channel();
    let dispose = std::thread::spawn(move || {
        p.dispose();
        let _ = done_tx.send(());
    });
    assert!(done_rx.recv_timeout(Duration::from_millis(200)).is_err(), "dispose returned while the check was still calling into mpv");
    answer_tx.send(()).unwrap();
    done_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    dispose.join().unwrap();
    check.join().unwrap();
}
