//! Copied into the pinned engine's ignored source checkout at build time.
//! Same borrowed-PCM ABI as orc-phonon, with an owned upstream six-thread pool.
const std = @import("std");
const Phonon = @import("embed/phonon.zig").Session;
const ThreadPool = @import("tts/phonon/parallel.zig").ThreadPool;
const clock = @import("util/time_compat.zig");
const allocator = std.heap.page_allocator;

const Handle = struct {
    phonon: Phonon,
    pool: ?*ThreadPool,
    cancelled: std.atomic.Value(u64) = .init(0),
};
const Result = extern struct {
    sample_rate: u32 = 24000,
    channels: u32 = 1,
    packets: u32 = 0,
    limited_chunks: u32 = 0,
    samples: u64 = 0,
    first_packet_seconds: f64 = 0,
    total_seconds: f64 = 0,
};
const Callback = *const fn ([*]const f32, usize, ?*anyopaque) callconv(.c) u8;

fn fail(buffer: ?[*]u8, capacity: usize, message: []const u8) void {
    if (capacity == 0) return;
    const out = buffer orelse return;
    const n = @min(capacity - 1, message.len);
    @memcpy(out[0..n], message[0..n]);
    out[n] = 0;
}

export fn orc_phonon_zzz_abi_version() u32 {
    return 1;
}

// Managed owner serializes synthesis and close; cancel may run concurrently.
export fn orc_phonon_zzz_open(path: ?[*:0]const u8, voice: ?[*:0]const u8, threads: u32, buffer: ?[*]u8, capacity: usize) ?*Handle {
    if (path == null or voice == null or threads == 0 or threads > 64) {
        fail(buffer, capacity, "Invalid Phonon bundle, voice or threads");
        return null;
    }
    const handle = allocator.create(Handle) catch |err| {
        fail(buffer, capacity, @errorName(err));
        return null;
    };
    const pool = if (threads > 1) ThreadPool.create(allocator, threads, null) catch |err| {
        allocator.destroy(handle);
        fail(buffer, capacity, @errorName(err));
        return null;
    } else null;
    handle.* = .{ .phonon = undefined, .pool = pool };
    handle.phonon.init(allocator, std.mem.span(path.?), std.mem.span(voice.?), .{
        .pool = pool,
        .temperature = null,
        .seed = 0,
    }) catch |err| {
        if (pool) |p| p.destroy();
        allocator.destroy(handle);
        fail(buffer, capacity, @errorName(err));
        return null;
    };
    // This trial is specifically the newer speaker-latent checkpoint.
    if (handle.phonon.model.profile != .speaker_latents) {
        orc_phonon_zzz_close(handle);
        fail(buffer, capacity, "Expected speaker-latent Phonon bundle");
        return null;
    }
    return handle;
}

const Delivery = struct {
    handle: *Handle,
    call: u64,
    callback: Callback,
    user: ?*anyopaque,
    result: *Result,
    started: i128,

    fn seconds(self: *const Delivery) f64 {
        return @as(f64, @floatFromInt(clock.nanoTimestamp() - self.started)) / 1e9;
    }
    fn write(raw: *anyopaque, pcm: []const f32) anyerror!void {
        const self: *Delivery = @ptrCast(@alignCast(raw));
        if (self.handle.cancelled.load(.acquire) == self.call) return error.Cancelled;
        if (pcm.len == 0) return;
        if (self.result.packets == 0) self.result.first_packet_seconds = self.seconds();
        if (self.callback(pcm.ptr, pcm.len, self.user) == 0) return error.Cancelled;
        self.result.packets += 1;
        self.result.samples += pcm.len;
    }
};

// Status: 0 complete, 1 cancelled, 2 limited (partial audio), -1 failure.
export fn orc_phonon_zzz_synthesize(handle: ?*Handle, text: ?[*:0]const u8, call: u64, callback: ?Callback, user: ?*anyopaque, output: ?*Result, buffer: ?[*]u8, capacity: usize) i32 {
    if (handle == null or text == null or call == 0 or callback == null or output == null) {
        fail(buffer, capacity, "Invalid Phonon call arguments");
        return -1;
    }
    const h = handle.?;
    const out = output.?;
    out.* = .{};
    const input = std.mem.span(text.?);
    if (input.len == 0 or input.len > 4096 or !std.unicode.utf8ValidateSlice(input)) {
        fail(buffer, capacity, "Expected 1..4096 UTF-8 bytes");
        return -1;
    }
    if (h.cancelled.load(.acquire) == call) return 1;
    var delivery: Delivery = .{ .handle = h, .call = call, .callback = callback.?, .user = user, .result = out, .started = clock.nanoTimestamp() };
    defer out.total_seconds = delivery.seconds();
    h.phonon.speaker.reseed(0);
    const report = h.phonon.speaker.speak(input, .en, .{ .context = &delivery, .write = Delivery.write }) catch |err| {
        if (err == error.Cancelled) return 1;
        fail(buffer, capacity, @errorName(err));
        return -1;
    };
    if (h.cancelled.load(.acquire) == call) return 1;
    out.limited_chunks = @intCast(report.limited);
    if (report.limited > 0) return 2;
    if (out.samples == 0) {
        fail(buffer, capacity, "Phonon returned empty audio");
        return -1;
    }
    return 0;
}

export fn orc_phonon_zzz_cancel(handle: ?*Handle, call: u64) void {
    if (handle) |h| h.cancelled.store(call, .release);
}

export fn orc_phonon_zzz_close(handle: ?*Handle) void {
    if (handle) |h| {
        h.phonon.deinit();
        if (h.pool) |p| p.destroy();
        allocator.destroy(h);
    }
}
