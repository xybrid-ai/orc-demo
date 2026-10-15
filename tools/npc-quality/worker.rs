//! Resident local Xybrid worker; JSON lines keep the scenario runner independent of FFI.
use anyhow::{Context, Result};
use serde_json::{json, Value};
use std::io::{self, BufRead, Write};
use std::time::Instant;
use xybrid_sdk::ir::{Envelope, EnvelopeKind, MessageRole};
use xybrid_sdk::{ConversationContext, GenerationConfig, ModelLoader};
fn main() -> Result<()> {
    let dir = std::env::args()
        .nth(1)
        .context("usage: npc-worker MODEL_DIRECTORY")?;
    let model = ModelLoader::from_directory(dir)?.load()?;
    let mut context = ConversationContext::new();
    let mut persona = String::new();
    for line in io::stdin().lock().lines() {
        let request: Value = serde_json::from_str(&line?)?;
        let system = request["persona"].as_str().context("persona required")?;
        if persona != system {
            persona = system.into();
            context = ConversationContext::new().with_system(
                Envelope::new(EnvelopeKind::Text(persona.clone())).with_role(MessageRole::System),
            );
        }
        let prompt = request["prompt"].as_str().context("prompt required")?;
        let input = Envelope::new(EnvelopeKind::Text(prompt.into())).with_role(MessageRole::User);
        let config = GenerationConfig {
            max_tokens: request["max_tokens"].as_u64().unwrap_or(112) as usize,
            temperature: 1.0,
            top_p: 0.95,
            min_p: 0.0,
            top_k: 64,
            repetition_penalty: 1.0,
            stop_sequences: vec!["<turn|>".into(), "<|turn>".into()],
            grammar: request["grammar"].as_str().map(String::from),
            ..GenerationConfig::default()
        };
        let start = Instant::now();
        let mut first = None;
        let mut events = Vec::new();
        let result = model.run_streaming_with_context(&input, &context, Some(&config), |token| {
            let elapsed = start.elapsed().as_secs_f64();
            first.get_or_insert(elapsed);
            events.push(json!({"seconds": elapsed, "text": token.token}));
            Ok(())
        });
        let response = match result {
            Ok(result) => {
                json!({"raw": match &result.envelope().kind {EnvelopeKind::Text(t) => t.clone(), _ => String::new()},
                "seconds": start.elapsed().as_secs_f64(), "ttft_seconds": first,
                "metadata": result.envelope().metadata, "events": events})
            }
            Err(error) => {
                json!({"error": error.to_string(), "seconds": start.elapsed().as_secs_f64(), "events": events})
            }
        };
        println!("{response}");
        io::stdout().flush()?;
    }
    Ok(())
}
