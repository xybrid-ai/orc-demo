#!/usr/bin/env python3
"""Gemma context experiment: variants A/B/C through one resident managed Xybrid 0.16.0 worker.

Only fixture inputs reach the C# builder and the model. Expectations stay in context-fixtures.json and the
scorer. Every request rebuilds its native context; nothing generated is pushed into a later request except the
ten-turn conversation's spoken history, exactly as the game records it. See README.md in this directory.
"""
import argparse, collections, hashlib, json, os, pathlib, platform, re, selectors, signal, subprocess, sys, time

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[2]
STORY = ROOT / "Assets/XybridAvatarDemo/Scenarios/TrolaAuthoredConversation.json"
FIXTURES = HERE / "context-fixtures.json"
ROTATIONS = ["ABC", "BCA", "CAB"]
VARIANTS = "ABC"
# FreeFormGatekeeperLineSource.Fallback: what the game speaks (and records) when the writer reply is rejected.
FALLBACKS = ["Mm. Go on. I'm listening.", "Careful. I still have a gate to guard.", "Tell me more. Keep the heroic bits short."]
CONTEXT_TOKENS, MAX_TOKENS, TURN_OVERHEAD = 8192, 112, 16
EXCHANGES = 6
MAX_CONSECUTIVE_TRANSPORT_FAILURES = 3


def sha(data):
    return hashlib.sha256(data if isinstance(data, bytes) else data.encode()).hexdigest()


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(8 << 20), b""):
            h.update(block)
    return h.hexdigest()


class JsonProcess:
    """One JSON request per line; one JSON reply per line."""
    def __init__(self, args, log, env=None):
        self.log = open(log, "w")
        self.process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log, text=True, env=env)

    def read(self, timeout):
        deadline = time.monotonic() + timeout
        with selectors.DefaultSelector() as ready:
            ready.register(self.process.stdout, selectors.EVENT_READ)
            while not ready.select(timeout=0.2):
                if self.process.poll() is not None:
                    raise RuntimeError("Process exited; see " + self.log.name)
                if time.monotonic() > deadline:
                    self.process.terminate()
                    raise TimeoutError("No reply within %s s; see %s" % (timeout, self.log.name))
        line = self.process.stdout.readline()
        if not line:
            raise RuntimeError("Process exited; see " + self.log.name)
        return json.loads(line)

    def ask(self, request, timeout=120):
        try:
            self.process.stdin.write(json.dumps(request) + "\n")
            self.process.stdin.flush()
        except BrokenPipeError:
            raise RuntimeError("Process exited; see " + self.log.name) from None
        return self.read(timeout)

    def close(self):
        """Best effort: never raises, escalates to SIGKILL."""
        try:
            try:
                self.process.stdin.close()
            except OSError:
                pass
            for stop in (None, self.process.terminate, self.process.kill):
                if stop:
                    stop()
                try:
                    self.process.wait(timeout=30 if stop is None else 10)
                    break
                except subprocess.TimeoutExpired:
                    continue
        except Exception as error:
            print("close failed: %r" % error, file=sys.stderr)
        finally:
            self.log.close()


def fixture_inputs(spec):
    """Merged inputs for the 18 independent fixtures; expectations are kept apart."""
    cases = []
    for f in spec["fixtures"]:
        inputs = json.loads(json.dumps(spec["defaults"]))
        inputs.update(f["input"])
        cases.append(dict(id=f["id"], category=f["category"], inputs=inputs, expect=f["expect"]))
    return cases


def roles(history):
    """Independent reimplementation of the role window, used to cross-check the C# builder."""
    starts = [i for i, h in enumerate(history) if h["speaker"] == "Player"]
    first = starts[-EXCHANGES] if len(starts) > EXCHANGES else 0
    out = []
    for h in history[first:]:
        role = {"Player": "user", "Troll": "assistant", "Trola": "assistant"}[h["speaker"]]
        if out and out[-1]["role"] == role:
            out[-1] = dict(role=role, text=out[-1]["text"] + "\n" + h["text"])
        else:
            out.append(dict(role=role, text=h["text"]))
    return out


def validate(built, variant, inputs):
    """Per-request transport checks, before inference. Returns the UTF-8 byte bound on input + output tokens."""
    message = inputs["message"]
    messages = built["messages"]
    window = roles(inputs["history"])
    texts = [built["persona"], built["prompt"]] + [m["text"] for m in messages]
    # Retained history may itself quote the current message (a model reply in the conversation); the builder adds exactly one more.
    echoes = sum(m["text"].count(message) for m in window)
    total = sum(t.count(message) for t in texts)
    if total != 1 + echoes:
        raise RuntimeError("current message must appear exactly once beyond history: %s %r (%d, %d)" % (variant, message, total, echoes))
    if not built["prompt"].rstrip("\n").endswith("Player's new message:\n" + message):
        raise RuntimeError("current message must end the final user turn: " + variant)
    if any(m["role"] not in ("user", "assistant") for m in messages) or any(a["role"] == b["role"] for a, b in zip(messages, messages[1:])):
        raise RuntimeError("native turns must be user/assistant and alternate: " + variant)
    if variant == "A" and messages:
        raise RuntimeError("A keeps history in its flattened prompt")
    if variant != "A" and (messages != window or (messages and messages[0]["role"] != "user")):
        raise RuntimeError("B/C turns must match the six-exchange window and start with the player: " + variant)
    budget = len(built["persona"].encode()) + len(built["prompt"].encode()) + sum(len(m["text"].encode()) + TURN_OVERHEAD for m in messages)
    # Tokens never exceed UTF-8 bytes, so this bound proves the request fits without clipping.
    return budget + 4 * TURN_OVERHEAD + MAX_TOKENS


def host_sample(stage):
    row = dict(stage=stage, unix=time.time())
    if platform.system() == "Darwin":
        for key, cmd in (("vm_stat", ["vm_stat"]), ("swap", ["sysctl", "vm.swapusage"]), ("thermal", ["pmset", "-g", "therm"])):
            try:
                row[key] = subprocess.run(cmd, capture_output=True, text=True, timeout=10).stdout
            except Exception as error:
                row[key] = repr(error)
        row["heavy_processes"] = subprocess.run(["ps", "-axo", "pid,rss,%cpu,comm", "-r"], capture_output=True, text=True).stdout.splitlines()[:12]
    return row


def run(cmd):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=30).stdout.strip()
    except Exception as error:
        return repr(error)


def environment(args, model_dir):
    assets = json.loads((ROOT / "local-assets.json").read_text())
    pins = {f["path"]: f.get("sha256") for f in assets["files"]}
    meta_path = model_dir / "model_metadata.json"
    meta = json.loads(meta_path.read_text())
    template = meta["execution_template"]
    if template["context_length"] != CONTEXT_TOKENS or meta["files"] != [template["model_file"]]:
        raise RuntimeError("Expected the staged 8192-token E2B Q4 metadata")
    gguf = model_dir / template["model_file"]
    hashes = dict(model=digest(gguf), metadata=digest(meta_path), native=digest(args.native))
    expected = dict(model=pins["Assets/StreamingAssets/XybridModels/gemma4npc-e2b-q4/Gemma4NPC-E2B-Q4_K_M.gguf"],
                    metadata=pins["Assets/StreamingAssets/XybridModels/gemma4npc-e2b-q4/model_metadata.json"],
                    native=pins["Assets/Xybrid/Plugins/macOS/libxybrid_bolt.dylib"])
    for key in hashes:
        if hashes[key] != expected[key]:
            raise RuntimeError("%s hash %s does not match local-assets.json %s" % (key, hashes[key], expected[key]))
    bolt = pathlib.Path(args.bindings)
    package_json = bolt.parent.parent / "package.json"
    package = json.loads(package_json.read_text())
    if package.get("name") != "ai.xybrid.sdk" or package.get("version") != assets["xybrid_sdk"]:
        raise RuntimeError("Bindings are not the pinned ai.xybrid.sdk %s package: %r" % (assets["xybrid_sdk"], package.get("version")))
    # The build manifest pins every compiled source and both assemblies; refuse to run anything else.
    manifest = json.loads(pathlib.Path(args.manifest).read_text())
    for name, value in manifest["sources"].items():
        path = bolt / name[5:] if name.startswith("bolt:") else ROOT / name
        if digest(path) != value:
            raise RuntimeError("Source changed since build: " + name)
    if digest(args.exporter) != manifest["built"]["ScenarioChecks.dll"] or digest(args.worker_dll) != manifest["built"]["NativeContextWorker.dll"]:
        raise RuntimeError("Built assemblies differ from the build manifest")
    top = run(["git", "-C", str(ROOT), "rev-parse", "--show-toplevel"])
    if top and pathlib.Path(top).resolve() == ROOT.resolve():
        git = dict(head=run(["git", "-C", str(ROOT), "rev-parse", "HEAD"]), dirty=run(["git", "-C", str(ROOT), "status", "--porcelain"]).splitlines())
    else:
        if not args.fixture_note:
            raise RuntimeError("ROOT is an exported fixture, not a git work tree: pass --fixture-note with its source commit and overlay")
        git = dict(head=None, reason="exported fixture; see fixture_note")
    return dict(
        model=dict(directory=str(model_dir), file=template["model_file"], bytes=gguf.stat().st_size, sha256=hashes["model"], metadata=meta,
                   metadata_sha256=hashes["metadata"]),
        native=dict(path=str(args.native), sha256=hashes["native"], bytes=os.path.getsize(args.native)),
        pinned_hashes_verified_against="local-assets.json",
        package=dict(name=package["name"], version=package["version"], package_json_sha256=digest(package_json)),
        bindings=dict(directory=str(bolt), combined_sha256=sha("".join(k + v for k, v in sorted(manifest["sources"].items()) if k.startswith("bolt:")))),
        build_manifest=manifest, runner_sha256=digest(__file__), fixtures_sha256=digest(FIXTURES),
        sampling=dict(context_tokens=CONTEXT_TOKENS, temperature=1, top_p=.95, top_k=64, min_p=0, repetition_penalty=1,
                      max_tokens=MAX_TOKENS, stops=["<turn|>", "<|turn>"], grammar="runtime WriterGrammar"),
        host=dict(platform=platform.platform(), machine=run(["sysctl", "-n", "machdep.cpu.brand_string"]),
                  memory=run(["sysctl", "-n", "hw.memsize"]), os=run(["sw_vers"]), dotnet=run([args.dotnet, "--version"]),
                  python=sys.version), git=git, fixture_note=args.fixture_note)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", required=True, type=pathlib.Path)
    p.add_argument("--native", required=True, help="Installed libxybrid_bolt.dylib (verified against local-assets.json)")
    p.add_argument("--bindings", required=True, help="Installed package Runtime/Bolt directory the worker was compiled against")
    p.add_argument("--manifest", default=str(ROOT / ".context/gemma-context/build/manifest.json"), help="From build_context.sh")
    p.add_argument("--exporter", required=True, help="ScenarioChecks.dll from build_context.sh")
    p.add_argument("--worker-dll", required=True, help="NativeContextWorker.dll from build_context.sh")
    p.add_argument("--dotnet", default="dotnet")
    p.add_argument("--output", required=True, type=pathlib.Path, help="Fresh receipt directory")
    p.add_argument("--fixture-note", default="", help="Required outside a git work tree: source commit and overlay")
    p.add_argument("--timeout", type=float, default=90, help="Native cancellation watchdog per generation, seconds")
    args = p.parse_args()
    if args.output.exists():
        raise FileExistsError("Use a fresh output directory: %s" % args.output)
    spec = json.loads(FIXTURES.read_text())
    cases = fixture_inputs(spec)
    if len(cases) != 18 or len({c["id"] for c in cases}) != 18:
        raise RuntimeError("Expected 18 distinct fixtures")
    env = environment(args, args.model)
    args.output.mkdir(parents=True)
    (args.output / "environment.json").write_text(json.dumps(env, indent=1))
    hosts = [host_sample("preflight")]
    receipts = (args.output / "receipts.jsonl").open("a")
    exporter = JsonProcess([args.dotnet, args.exporter, str(STORY)], args.output / "exporter.log")
    worker_env = dict(os.environ, NPC_NATIVE_LIB=str(pathlib.Path(args.native).resolve()))
    counters = collections.Counter()
    state = dict(worker=None, previous=None, consecutive_failures=0)

    def save(row):
        receipts.write(json.dumps(row) + "\n")
        receipts.flush()
        line = (row.get("line") or {}).get("text") or (row.get("raw") or row.get("error") or "")[:100]
        print("%-4s %-22s %-3s r%s %5.2fs %-5s %s" % (row.get("kind", "")[:4], row.get("id"), row.get("variant", "-"), row.get("repeat", "-"),
                                                     row.get("seconds") or 0, row.get("valid"), line), flush=True)

    def ask_worker(request, timeout):
        """Every native request, counted or not, so each receipt records what ran just before it (cache provenance)."""
        result = state["worker"].ask(request, timeout=timeout)
        if result.get("id") != request.get("id"):
            raise RuntimeError("worker reply out of order")
        result["previous_request"] = state["previous"]
        state["previous"] = dict(id=request.get("id"), sha256=sha(json.dumps([request.get("persona"), request.get("messages"), request.get("prompt")])))
        return result

    def reset_cache():
        # Xybrid 0.16.0 reuses a KV prefix between runs and exposes no cached-token count. A one-token run on an unrelated
        # card leaves no shared prefix, so every counted generation starts cold and timing is comparable across variants.
        r = ask_worker(dict(id="kv-reset-%d" % counters["kv_resets"], persona="Cache reset.", messages=[], prompt="x", grammar=None,
                            max_tokens=1, timeout_seconds=args.timeout), args.timeout + 90)
        counters["kv_resets"] += 1
        if r.get("error"):
            raise RuntimeError("cache reset failed: %s" % r["error"])

    try:
        # Pre-inference validation: C# construction checks over every fixture, then native context checks.
        checks = exporter.ask(dict(command="context_checks", fixtures=[c["inputs"] for c in cases]))
        if not checks.get("passed"):
            raise RuntimeError(checks)
        native = subprocess.run([args.dotnet, args.worker_dll, "--context-checks"], capture_output=True, text=True, env=worker_env, timeout=120)
        native_checks = json.loads(native.stdout.strip().splitlines()[-1]) if native.returncode == 0 else dict(passed=False, stderr=native.stderr)
        if not native_checks.get("passed"):
            raise RuntimeError(native_checks)
        (args.output / "checks.json").write_text(json.dumps(dict(construction=checks, native=native_checks), indent=1))
        print("Checks passed: %d construction, %d native" % (len(checks["checks"]), len(native_checks["checks"])), flush=True)

        state["worker"] = JsonProcess([args.dotnet, args.worker_dll, str(args.model.resolve())], args.output / "worker.log", worker_env)
        loaded = time.monotonic()
        if not state["worker"].read(600).get("ready"):
            raise RuntimeError("worker not ready")
        load_seconds = time.monotonic() - loaded

        def generate(variant, case_id, inputs, kind, repeat=1, position=None, order=None, expect=None, extra=None):
            built = exporter.ask(dict(command="context_build", variant=variant, fixture=inputs))
            budget = validate(built, variant, inputs)
            request = dict(id="%s/%s/%s/r%d" % (kind, case_id, variant, repeat), persona=built["persona"], messages=built["messages"],
                           prompt=built["prompt"], grammar=built["grammar"], max_tokens=MAX_TOKENS, timeout_seconds=args.timeout)
            row = dict(kind=kind, id=case_id, variant=variant, repeat=repeat, position=position, order=order, inputs=inputs,
                       expect=expect, plan=built["plan"], transcript=built["transcript"], persona=built["persona"],
                       messages=built["messages"], prompt=built["prompt"], persona_sha256=sha(built["persona"]),
                       request_sha256=sha(json.dumps([built["persona"], built["messages"], built["prompt"], built["grammar"]])),
                       grammar_sha256=sha(built["grammar"]), byte_bound=budget, **(extra or {}))
            counters["generations"] += 1
            if budget > CONTEXT_TOKENS:
                counters["overflow_rejected"] += 1
                row.update(rejected="context overflow bound %d > %d; not run" % (budget, CONTEXT_TOKENS), valid=False, transport_failure="overflow")
                save(row)
                return row
            reset_cache()
            result = ask_worker(request, args.timeout + 90)
            failure = "timeout" if result.get("timed_out") else "error" if result.get("error") else None
            if result.get("native_history") is not None and result["native_history"] != built["messages"]:
                raise RuntimeError("native context differs from the request")
            parsed = exporter.ask(dict(command="context_parse", fixture=inputs, raw=result.get("raw") or ""))
            row.update({k: v for k, v in result.items() if k != "id"})
            row.update(valid=parsed["valid"] and not failure, parsed_text=parsed.get("text"), line=parsed.get("line") if not failure else None,
                       transport_failure=failure, kv_reset_before=True)
            if failure:
                counters["transport_" + failure] += 1
                state["consecutive_failures"] += 1
                if state["consecutive_failures"] >= MAX_CONSECUTIVE_TRANSPORT_FAILURES:
                    save(row)
                    raise RuntimeError("Native inference failing repeatedly; see receipts and worker.log")
            else:
                state["consecutive_failures"] = 0
            cumulative = ""
            row["text_ready_seconds"] = None
            for event in row.get("events", []):
                cumulative += event.get("text") or ""
                prefix = re.match(r'^\s*\{\s*"text"\s*:\s*', cumulative)
                if prefix:
                    try:
                        value, _ = json.JSONDecoder().raw_decode(cumulative[prefix.end():])
                        if isinstance(value, str):
                            row["text_ready_seconds"] = event["seconds"]
                            break
                    except json.JSONDecodeError:
                        pass
            save(row)
            return row

        # Isolation without inference: a populated context, then an empty one, through the resident worker.
        probe = roles([dict(speaker="Player", text="Remember me as Bob."), dict(speaker="Troll", text="Bob. Noted.")])
        held = ask_worker(dict(command="inspect", id="inspect-1", persona="isolation", messages=probe), 60)
        empty = ask_worker(dict(command="inspect", id="inspect-2", persona="isolation", messages=[]), 60)
        if not (held.get("native_history") == probe and empty.get("native_history") == [] and held["context_id"] != empty["context_id"]):
            raise RuntimeError((held, empty))

        # The runtime's discarded prime: its character card, "Greetings", one token, no grammar. Identical for every variant.
        prime = exporter.ask(dict(command="context_prime"))
        result = ask_worker(dict(id="prime", persona=prime["persona"], messages=[], prompt=prime["prompt"], grammar=None, max_tokens=1,
                                 timeout_seconds=args.timeout + 300), args.timeout + 390)
        save(dict(kind="prime", id="prime", load_seconds=load_seconds, persona_sha256=sha(prime["persona"]), prompt=prime["prompt"],
                  **{k: v for k, v in result.items() if k != "id"}))
        if result.get("error"):
            raise RuntimeError(result)

        # Native cancellation diagnostic (not counted): a long ungrammared non-fixture reply, cancelled after its first token.
        # The run must stop early natively; then an ordinary run must still work.
        long_ask = "Describe every stone of your wall, one by one, in as much detail as you can."
        cancelled = ask_worker(dict(id="cancel-check", persona=prime["persona"], messages=[], prompt=long_ask, grammar=None,
                                    max_tokens=MAX_TOKENS, timeout_seconds=args.timeout, cancel_after_tokens=1), args.timeout + 90)
        emitted = [e for e in cancelled.get("events", []) if e.get("text")]
        late = [e for e in emitted if cancelled.get("cancel_requested_seconds") is not None and e["seconds"] > cancelled["cancel_requested_seconds"]]
        recovered = ask_worker(dict(id="post-cancel", persona=prime["persona"], messages=[], prompt=prime["prompt"], grammar=None, max_tokens=1,
                                    timeout_seconds=args.timeout), args.timeout + 90)
        stopped = (cancelled.get("cancel_requested_seconds") is not None and not cancelled.get("timed_out")
                   and len(emitted) < MAX_TOKENS // 2 and not recovered.get("error") and recovered.get("native_history") == [])
        save(dict(kind="diagnostic", id="cancel-check", tokens_emitted=len(emitted), tokens_after_cancel=len(late), native_stopped=stopped,
                  recovered_error=recovered.get("error"), recovered_seconds=recovered.get("seconds"), **{k: v for k, v in cancelled.items() if k != "id"}))
        if not stopped:
            raise RuntimeError("Native cancellation did not stop the run early, or the worker did not recover")
        hosts.append(host_sample("after-prime"))

        # 18 fixtures x 3 variants x 3 repetitions, variant order rotated ABC, BCA, CAB.
        for repeat, order in enumerate(ROTATIONS, 1):
            for case in cases:
                for position, variant in enumerate(order):
                    generate(variant, case["id"], case["inputs"], "independent", repeat, position, order, case["expect"],
                             dict(category=case["category"]))
            hosts.append(host_sample("after-repeat-%d" % repeat))

        # One identical ten-turn conversation per variant. History holds the variant's own accepted replies; a rejected
        # reply records the game's authored fallback, as FreeFormGatekeeperLineSource and the controller do.
        sequence = spec["sequence"]
        for variant in VARIANTS:
            history, offer, fallback = [], None, 0
            for turn_index, turn in enumerate(sequence["turns"], 1):
                if turn_index == sequence["offer_from_turn"]:
                    offer = sequence["offer"]  # the trusted offer action accompanies the player's turn-3 offer
                inputs = json.loads(json.dumps(spec["defaults"]))
                inputs.update(message=turn["message"], history=list(history), offer=offer)
                row = generate(variant, "sequence_%d" % turn_index, inputs, "sequence", 1, None, None, turn["expect"], dict(turn=turn_index))
                if row.get("valid"):
                    spoken = row["line"]["text"]
                else:
                    spoken, fallback = FALLBACKS[fallback % len(FALLBACKS)], fallback + 1
                    row_fallback = dict(kind="fallback", id=row["id"], variant=variant, spoken=spoken,
                                        reason=row.get("transport_failure") or "parser")
                    receipts.write(json.dumps(row_fallback) + "\n")
                history += [dict(speaker="Player", text=turn["message"]), dict(speaker="Troll", text=spoken)]
        hosts.append(host_sample("after-sequence"))

        probes = spec["history_probes"]
        for variant in VARIANTS:
            for count in probes["exchanges"]:
                history = []
                for i in range(count):
                    history += [dict(speaker="Player", text=probes["player"].format(i=i)), dict(speaker="Troll", text=probes["npc"].format(i=i))]
                inputs = json.loads(json.dumps(spec["defaults"]))
                inputs.update(message=probes["message"], history=history)
                generate(variant, "history_%d" % count, inputs, "history", 1, None, None, probes["expect"], dict(exchanges=count))
        hosts.append(host_sample("complete"))
        if counters["generations"] != 204:
            raise RuntimeError(counters)
        failures = sum(v for k, v in counters.items() if k.startswith("transport_")) + counters["overflow_rejected"]
        (args.output / "summary.json").write_text(json.dumps(dict(status="complete" if not failures else "complete_with_transport_failures",
                                                                  load_seconds=load_seconds, **counters), indent=1))
    except BaseException as error:
        (args.output / "summary.json").write_text(json.dumps(dict(status="failed", error=repr(error), **counters), indent=1))
        raise
    finally:
        (args.output / "host.json").write_text(json.dumps(hosts, indent=1))
        if state["worker"]:
            state["worker"].close()
        exporter.close()
        receipts.close()


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(143))
    main()
