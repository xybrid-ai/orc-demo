const std = @import("std");

pub fn build(b: *std.Build) void {
    const engine = b.option([]const u8, "engine-path", "Pinned zzz checkout") orelse @panic("engine-path required");
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const options = b.addOptions();
    options.addOption(bool, "phonon_profile", false);
    options.addOption(bool, "pool_profile", false);
    options.addOption(u32, "pool_chunk_factor", 4);
    options.addOption(bool, "pool_chunk_factor_explicit", true);
    options.addOption(bool, "hot_region", true);
    options.addOption(bool, "perf_core_pin", true);
    options.addOption(u32, "prefill_nsplit_max_m", 0);
    options.addOption(bool, "q8k_act", true);
    options.addOption(bool, "q5k_rows4", false);
    options.addOption(bool, "sdpa_prefill_gqa2", false);
    const root = b.createModule(.{
        .root_source_file = .{ .cwd_relative = b.fmt("{s}/src/orc_phonon_bridge.zig", .{engine}) },
        .target = target,
        .optimize = optimize,
        .link_libc = true,
        .pic = true,
    });
    root.addOptions("zzz_build_options", options);
    root.addImport("cpu_f32_kernels", b.createModule(.{
        .root_source_file = .{ .cwd_relative = b.fmt("{s}/src/backend/cpu/kernels/f32.zig", .{engine}) },
        .target = target,
        .optimize = optimize,
    }));
    const lib = b.addLibrary(.{ .name = "orc_phonon_zzz", .linkage = .dynamic, .root_module = root });
    lib.bundle_compiler_rt = true;
    b.installArtifact(lib);
}
