//! CPU Phonon bridge for the Orc demo's supplied checkpoint and cloned voice.
//! Borrowed PCM exists only during callbacks; dropping a stream joins its workers.

use ptts::checkpoint::{Checkpoint, ResolveOptions};
use ptts::preprocess::Lang;
use ptts::synth::{DeviceKind, Quant, Synth};
use std::ffi::{c_char, c_void, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::Instant;

struct Handle {
    synth: Mutex<Synth>,
    cancelled_call: AtomicU64,
}

#[repr(C)]
#[derive(Debug, Default)]
pub struct AudioResult {
    sample_rate: u32,
    channels: u32,
    chunks: u32,
    reserved: u32,
    samples: u64,
    first_packet_seconds: f64,
    total_seconds: f64,
}

type AudioCallback = unsafe extern "C" fn(*const f32, usize, *mut c_void) -> u8;

// The caller owns and pins this writable buffer for the entire FFI call.
unsafe fn error(buffer: *mut u8, capacity: usize, message: &str) {
    if !buffer.is_null() && capacity > 0 {
        let bytes = message.as_bytes();
        let length = bytes.len().min(capacity - 1);
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, length);
            *buffer.add(length) = 0;
        }
    }
}

unsafe fn text<'a>(pointer: *const c_char) -> Result<&'a str, String> {
    if pointer.is_null() {
        return Err("Missing UTF-8 input".into());
    }
    // FFI contract: the caller pins a NUL-terminated UTF-8 string through return.
    unsafe { CStr::from_ptr(pointer) }
        .to_str()
        .map_err(|e| e.to_string())
}

#[no_mangle]
pub extern "C" fn orc_phonon_abi_version() -> u32 {
    1
}

/// Open a CPU/Q8 checkpoint and register its supplied speaker-latent voice.
///
/// # Safety
/// Inputs must be NUL-terminated strings. The error buffer must hold `capacity` bytes.
#[no_mangle]
pub unsafe extern "C" fn orc_phonon_open(
    directory: *const c_char,
    voice: *const c_char,
    threads: u32,
    buffer: *mut u8,
    capacity: usize,
) -> *mut c_void {
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<Handle, String> {
        if !(1..=64).contains(&threads) {
            return Err("Phonon threads must be between 1 and 64".into());
        }
        let directory = unsafe { text(directory)? };
        let voice = unsafe { text(voice)? };
        xn::utils::set_num_threads(threads as usize);
        let checkpoint = Checkpoint::resolve(
            Path::new(directory),
            ResolveOptions {
                quant: Quant::Q80,
                weights: None,
            },
        )
        .map_err(|e| e.to_string())?;
        let synth = checkpoint
            .builder(Lang::En)
            .device(DeviceKind::Cpu)
            .quant(Quant::Q80)
            .temperature(0.3)
            .seed(0)
            .add_voice("trola_clone", voice)
            .voice("trola_clone")
            .build()
            .map_err(|e| e.to_string())?;
        if synth.sample_rate() != 24000 {
            return Err("Expected 24 kHz Phonon PCM".into());
        }
        Ok(Handle {
            synth: Mutex::new(synth),
            cancelled_call: AtomicU64::new(0),
        })
    }));
    match result {
        Ok(Ok(handle)) => Box::into_raw(Box::new(handle)).cast(),
        Ok(Err(message)) => {
            unsafe { error(buffer, capacity, &message) };
            std::ptr::null_mut()
        }
        Err(_) => {
            unsafe { error(buffer, capacity, "Phonon panicked during model opening") };
            std::ptr::null_mut()
        }
    }
}

/// Generate fresh audio, returning 0 for complete, 1 for cancelled, -1 for failure.
///
/// # Safety
/// The handle must remain open through return. Strings and output buffers must remain valid.
/// The callback must copy borrowed samples and must not unwind or close the handle.
#[no_mangle]
pub unsafe extern "C" fn orc_phonon_synthesize(
    handle: *const c_void,
    input: *const c_char,
    call: u64,
    callback: Option<AudioCallback>,
    user: *mut c_void,
    output: *mut AudioResult,
    buffer: *mut u8,
    capacity: usize,
) -> i32 {
    if handle.is_null() || output.is_null() || call == 0 || callback.is_none() {
        unsafe { error(buffer, capacity, "Invalid Phonon call arguments") };
        return -1;
    }
    // Each handle is a Box<Handle> until close; the managed owner serializes calls and close.
    let handle = unsafe { &*handle.cast::<Handle>() };
    unsafe { output.write(AudioResult::default()) };
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<i32, String> {
        let started = Instant::now();
        let input = unsafe { text(input)? };
        if input.trim().is_empty() || input.len() > 4096 {
            return Err("Expected a nonempty spoken clause of at most 4096 UTF-8 bytes".into());
        }
        let synth = handle
            .synth
            .lock()
            .map_err(|_| "Phonon session lock poisoned")?;
        let cancelled = || handle.cancelled_call.load(Ordering::Acquire) == call;
        if cancelled() {
            return Ok(1);
        }
        let mut stream = synth.stream(input).map_err(|e| e.to_string())?;
        let mut audio = AudioResult {
            sample_rate: 24000,
            channels: 1,
            ..Default::default()
        };
        let callback = callback.ok_or("Missing audio callback")?;
        let mut status = 0;
        while let Some(packet) = stream.next() {
            if cancelled() {
                status = 1;
                break;
            }
            let pcm = packet.map_err(|e| e.to_string())?;
            if pcm.is_empty() {
                continue;
            }
            if audio.chunks == 0 {
                audio.first_packet_seconds = started.elapsed().as_secs_f64();
            }
            // The slice is borrowed only during this callback, and never crosses asynchronously.
            if unsafe { callback(pcm.as_ptr(), pcm.len(), user) } == 0 {
                status = 1;
                break;
            }
            audio.samples += pcm.len() as u64;
            audio.chunks += 1;
        }
        drop(stream); // Stops and joins both upstream workers before returning or releasing synth.
        if cancelled() {
            status = 1;
        }
        audio.total_seconds = started.elapsed().as_secs_f64();
        unsafe { output.write(audio) };
        if status == 0 && unsafe { (*output).samples } == 0 {
            return Err("Phonon returned empty audio".into());
        }
        Ok(status)
    }));
    match result {
        Ok(Ok(status)) => status,
        Ok(Err(message)) => {
            unsafe { error(buffer, capacity, &message) };
            -1
        }
        Err(_) => {
            unsafe { error(buffer, capacity, "Phonon panicked during synthesis") };
            -1
        }
    }
}

/// Cancel a specific call, including cancellation racing call entry.
///
/// # Safety
/// The handle must remain open through this call; `call` identifies the intended utterance.
#[no_mangle]
pub unsafe extern "C" fn orc_phonon_cancel(handle: *const c_void, call: u64) {
    if !handle.is_null() {
        // Cancellation has no model lock: a blocked synthesis may still be cancelled.
        unsafe { &*handle.cast::<Handle>() }
            .cancelled_call
            .store(call, Ordering::Release);
    }
}

/// Release a handle after all synthesis and cancellation calls have returned.
///
/// # Safety
/// The pointer must come from open and be closed exactly once, with no concurrent users.
#[no_mangle]
pub unsafe extern "C" fn orc_phonon_close(handle: *mut c_void) {
    if !handle.is_null() {
        // Sole ownership returns from the managed owner only after its serial gate has drained.
        drop(unsafe { Box::from_raw(handle.cast::<Handle>()) });
    }
}
