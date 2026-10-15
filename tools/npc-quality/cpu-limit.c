// Linux benchmark only: bound std::thread::hardware_concurrency() in the
// native backend to the requested worker count. No SDK source is changed.
#include <stdlib.h>
unsigned int npc_hardware_concurrency(void) __asm__("_ZNSt6thread20hardware_concurrencyEv");
unsigned int npc_hardware_concurrency(void) {
    const char *value = getenv("NPC_BENCH_THREADS");
    unsigned int threads = value ? (unsigned int)strtoul(value, 0, 10) : 4;
    return threads > 0 && threads <= 64 ? threads : 4;
}
