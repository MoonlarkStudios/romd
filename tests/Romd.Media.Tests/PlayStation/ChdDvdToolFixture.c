/* Synthetic Linux foreign-process payload, compiled and recorded by the test helper
   with Clang. This is NOT chdman: createdvd emits header-only
   CHD and extractdvd emits deterministic bytes from a header-only synthetic input. */
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include <signal.h>
#include <stdint.h>
#include <time.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/file.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
extern char **environ;

static void be32(unsigned char *p, uint32_t v) {
    for (int i = 3; i >= 0; --i) { p[i] = (unsigned char)v; v >>= 8; }
}
static void be64(unsigned char *p, uint64_t v) {
    for (int i = 7; i >= 0; --i) { p[i] = (unsigned char)v; v >>= 8; }
}
static uint64_t read_be64(const unsigned char *p) {
    uint64_t result = 0;
    for (int i = 0; i < 8; ++i) result = (result << 8) | p[i];
    return result;
}
static int write_all(int fd, const void *bytes, size_t count) {
    const unsigned char *p = bytes;
    while (count != 0) {
        ssize_t n = write(fd, p, count);
        if (n < 0 && errno == EINTR) continue;
        if (n <= 0) return -1;
        p += n; count -= (size_t)n;
    }
    return 0;
}
static int publish(const char *source, const char *suffix, const char *bytes, size_t count) {
    char target[2048], temporary[2048];
    if (snprintf(target, sizeof target, "%s%s", source, suffix) >= (int)sizeof target ||
        snprintf(temporary, sizeof temporary, "%s.tmp", target) >= (int)sizeof temporary) return -1;
    int fd = open(temporary, O_WRONLY | O_CREAT | O_EXCL, 0600);
    if (fd < 0) return -1;
    int result = write_all(fd, bytes, count);
    if (close(fd) != 0) result = -1;
    return result == 0 ? rename(temporary, target) : result;
}
static int limit(int kind, struct rlimit *value) { return getrlimit(kind, value); }
static int flood(int fd, size_t count) {
    unsigned char bytes[4096]; memset(bytes, 'X', sizeof bytes);
    while (count != 0) {
        size_t n = count < sizeof bytes ? count : sizeof bytes;
        if (write_all(fd, bytes, n) != 0) return -1;
        count -= n;
    }
    return write_all(fd, "\n", 1);
}
static volatile sig_atomic_t cpu_warning_received;
static void observe_cpu_warning(int number) { (void)number; cpu_warning_received = 1; }

int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "--abi") == 0) {
        printf("{\"StatSize\":%zu,\"StatModeOffset\":%zu,\"ONoFollow\":%d,\"ONonBlock\":%d,\"SIfMt\":%u,\"SIfReg\":%u}\n",
            sizeof(struct stat), offsetof(struct stat, st_mode), O_NOFOLLOW, O_NONBLOCK, (unsigned)S_IFMT, (unsigned)S_IFREG);
        return 0;
    }
    int extract = argc == 6 && strcmp(argv[1], "extractdvd") == 0 && strcmp(argv[2], "-i") == 0 && strcmp(argv[4], "-o") == 0;
    if (!extract && (argc != 12 || strcmp(argv[1], "createdvd") != 0 || strcmp(argv[2], "-i") != 0 ||
        strcmp(argv[4], "-o") != 0 || strcmp(argv[6], "-hs") != 0 || strcmp(argv[7], "16384") != 0 ||
        strcmp(argv[8], "-c") != 0 || strcmp(argv[9], "zstd") != 0 || strcmp(argv[10], "-np") != 0 ||
        strcmp(argv[11], "1") != 0)) return 70;
    /* Before opening any payload file: the launcher's sealed images are chdman at 4 and prlimit at 5. */
    int extra = fcntl(6, F_GETFD) >= 0;
    int tool_seals = fcntl(4, F_GET_SEALS), wrapper_seals = fcntl(5, F_GET_SEALS);
    struct stat executed, tool_image;
    int executes_tool_image = stat("/proc/self/exe", &executed) == 0 && fstat(4, &tool_image) == 0 &&
        executed.st_dev == tool_image.st_dev && executed.st_ino == tool_image.st_ino;
    int lease_flags = fcntl(3, F_GETFD), status_flags = fcntl(3, F_GETFL);
    char nonce[33] = {0};
    if (lease_flags != 0 || status_flags < 0 || pread(3, nonce, 32, 0) != 32) return 72;
    if (fcntl(3, F_SETFL, status_flags | O_NONBLOCK) != 0) return 73;
    status_flags = fcntl(3, F_GETFL);
    char lease_path[2048]; ssize_t lease_length = readlink("/proc/self/fd/3", lease_path, sizeof lease_path - 1);
    if (lease_length <= 0) return 74;
    lease_path[lease_length] = '\0';
    int independent = open(lease_path, O_RDWR);
    if (independent < 0) return 75;
    int denied = flock(independent, LOCK_EX | LOCK_NB) != 0 && (errno == EWOULDBLOCK || errno == EAGAIN);
    close(independent);
    if (!denied) return 76;
    struct rlimit fsize, address, data, cpu, core;
    if (limit(RLIMIT_FSIZE, &fsize) || limit(RLIMIT_AS, &address) || limit(RLIMIT_DATA, &data) ||
        limit(RLIMIT_CPU, &cpu) || limit(RLIMIT_CORE, &core)) return 77;
    int env_count = 0; while (environ[env_count] != NULL) ++env_count;
    errno = 0; int nice_value = getpriority(PRIO_PROCESS, 0);
    if (errno != 0) return 78;
    const char *source = argv[3], *output = argv[5];
    int input = open(source, O_RDONLY);
    struct stat original;
    char command[64] = {0}; int mode;
    unsigned char header[124] = {0};
    if (input < 0 || fstat(input, &original) != 0 || pread(input, command, sizeof command - 1, extract ? 124 : 0) < 0 ||
        sscanf(command, "ROMD-DVD-FIXTURE:%d", &mode) != 1 ||
        (extract && pread(input, header, sizeof header, 0) != (ssize_t)sizeof header)) return 79;
    uint64_t logical_bytes = extract ? read_be64(header + 32) : (uint64_t)original.st_size;
    close(input);
    char report[2048];
    int length = snprintf(report, sizeof report,
        "{\"Pid\":%ld,\"GroupId\":%ld,\"Nice\":%d,\"Nonce\":\"%s\",\"LeaseFlags\":%d,"
        "\"StatusFlags\":%d,\"ExtraDescriptor\":%d,\"ToolImageSeals\":%d,\"WrapperImageSeals\":%d,"
        "\"ExecutesToolImage\":%d,\"EnvironmentCount\":%d,\"FlockDenied\":%d,"
        "\"StatSize\":%zu,\"StatModeOffset\":%zu,"
        "\"FsizeSoft\":%" PRIu64 ",\"FsizeHard\":%" PRIu64 ",\"AddressSpaceSoft\":%" PRIu64 ","
        "\"AddressSpaceHard\":%" PRIu64 ",\"DataSoft\":%" PRIu64 ",\"DataHard\":%" PRIu64 ","
        "\"CpuSoft\":%" PRIu64 ",\"CpuHard\":%" PRIu64 ",\"CoreSoft\":%" PRIu64 ",\"CoreHard\":%" PRIu64 "}\n",
        (long)getpid(), (long)getpgrp(), nice_value, nonce, lease_flags, status_flags, extra, tool_seals, wrapper_seals, executes_tool_image, env_count, denied, sizeof(struct stat), offsetof(struct stat, st_mode),
        (uint64_t)fsize.rlim_cur, (uint64_t)fsize.rlim_max, (uint64_t)address.rlim_cur, (uint64_t)address.rlim_max,
        (uint64_t)data.rlim_cur, (uint64_t)data.rlim_max, (uint64_t)cpu.rlim_cur, (uint64_t)cpu.rlim_max,
        (uint64_t)core.rlim_cur, (uint64_t)core.rlim_max);
    if (length < 0 || length >= (int)sizeof report || publish(source, ".facts", report, (size_t)length) != 0) return 80;
    char pid_text[32]; int pid_length = snprintf(pid_text, sizeof pid_text, "%ld", (long)getpid());
    if (pid_length <= 0 || publish(source, ".pid", pid_text, (size_t)pid_length) != 0) return 81;
    if (write_all(STDOUT_FILENO, report, (size_t)length) != 0) return 82;
    if (mode == 23) {
        /* A short native execution without the test-only observation handshake. */
        struct rusage first, current;
        if (getrusage(RUSAGE_SELF, &first) != 0) return 106;
        uint64_t started = (uint64_t)first.ru_utime.tv_sec * 1000000 + first.ru_utime.tv_usec +
            (uint64_t)first.ru_stime.tv_sec * 1000000 + first.ru_stime.tv_usec;
        uint64_t elapsed;
        do {
            for (volatile uint64_t spin = 0; spin < 1000; ++spin) { }
            if (getrusage(RUSAGE_SELF, &current) != 0) return 107;
            elapsed = (uint64_t)current.ru_utime.tv_sec * 1000000 + current.ru_utime.tv_usec +
                (uint64_t)current.ru_stime.tv_sec * 1000000 + current.ru_stime.tv_usec - started;
        } while (elapsed < 5000);
    } else {
        /* Other fixtures wait until owned image observation closes stdin. */
        char unexpected;
        ssize_t received;
        do { received = read(STDIN_FILENO, &unexpected, 1); } while (received < 0 && errno == EINTR);
        if (received != 0) return 96;
    }
    if (mode == 1) return 23;
    if (mode == 17 || mode == 18) {
        /* Inherit the test's prlimit soft/hard CPU values, then let the kernel enforce them. */
        for (volatile uint64_t spin = 0;; ++spin) { }
    }
    if (mode == 20 || mode == 21) {
        /* Keep the original soft limit, but establish positive evidence on both CPU clocks.
           A finite owned wall deadline still bounds this synthetic test-only process. */
        struct sigaction action = {0};
        action.sa_handler = observe_cpu_warning;
        if (sigemptyset(&action.sa_mask) != 0 || sigaction(SIGXCPU, &action, NULL) != 0) return 104;
        int reported_warning = 0;
        for (volatile uint64_t spin = 0;; ++spin) {
            if ((spin & 16383) != 0 || !cpu_warning_received) continue;
            if (!reported_warning) {
                static const char warning[] = "cpu-first-warning\n";
                if (write_all(STDOUT_FILENO, warning, sizeof warning - 1) != 0) return 105;
                reported_warning = 1;
            }
            struct rusage usage;
            if (getrusage(RUSAGE_SELF, &usage) != 0) return 106;
            uint64_t used = ((uint64_t)usage.ru_utime.tv_sec + (uint64_t)usage.ru_stime.tv_sec) * 1000000
                + (uint64_t)usage.ru_utime.tv_usec + (uint64_t)usage.ru_stime.tv_usec;
            struct timespec profiling;
            clockid_t profiling_clock = (clockid_t)((~(uint32_t)getpid()) << 3);
            if (clock_gettime(profiling_clock, &profiling) != 0) return 111;
            uint64_t profiled = (uint64_t)profiling.tv_sec * 1000000 + (uint64_t)profiling.tv_nsec / 1000;
            if (used >= 1250000 && profiled >= 1250000) break;
        }
        static const char ready[] = "cpu-evidence-ready\n";
        if (write_all(STDOUT_FILENO, ready, sizeof ready - 1) != 0) return 107;
        if (mode == 20) {
            action.sa_handler = SIG_DFL;
            if (sigaction(SIGXCPU, &action, NULL) != 0) return 108;
        } else {
            const struct rlimit exhausted = {1, 1};
            if (setrlimit(RLIMIT_CPU, &exhausted) != 0) return 109;
        }
        /* Both exits are kernel enforcement, not self-sent signals. */
        for (volatile uint64_t spin = 0;; ++spin) { }
    }
    if (mode == 24) { raise(SIGXCPU); return 110; } /* External signal, without consuming the budget. */
    if (mode == 19) { raise(SIGKILL); return 103; } /* External kill, without consuming the budget. */
    if (mode == 9) { for (;;) pause(); }
    if (mode == 5 || mode == 14) {
        if (flood(mode == 5 ? STDOUT_FILENO : STDERR_FILENO, 65536) != 0) return 83;
    }
    if (mode == 6 || mode == 7) {
        if (flood(mode == 6 ? STDOUT_FILENO : STDERR_FILENO, 262145) != 0) return 84;
        for (;;) pause();
    }
    if (mode == 10 || mode == 11) {
        int changed = open(source, O_RDWR);
        unsigned char value;
        if (changed < 0) return 85;
        int result = mode == 11 ? ftruncate(changed, 0) :
            (pread(changed, &value, 1, 128) == 1 ? (value ^= 1, pwrite(changed, &value, 1, 128) == 1 ? 0 : -1) : -1);
        close(changed); if (result != 0) return 86;
    }
    if (mode == 16) {
        /* Mutate the held inode, then hide that mutation behind an untouched same-length
           replacement prepared by the caller. The child never copies the input. */
        int changed = open(source, O_RDWR);
        unsigned char value;
        struct stat replacement;
        char staged[2048];
        if (changed < 0 || pread(changed, &value, 1, 256) != 1) return 100;
        value ^= 1;
        int result = pwrite(changed, &value, 1, 256) == 1 ? 0 : -1;
        if (close(changed) != 0) result = -1;
        if (result != 0 || snprintf(staged, sizeof staged, "%s.replacement", source) >= (int)sizeof staged ||
            stat(staged, &replacement) != 0 || replacement.st_size != original.st_size || rename(staged, source) != 0) return 101;
    }
    if (!extract && mode == 15) {
        /* The output size cap forbids copying the input, so rename a caller-staged replacement over its name. */
        char staged[2048];
        if (snprintf(staged, sizeof staged, "%s.replacement", source) >= (int)sizeof staged || rename(staged, source) != 0) return 99;
    }
    if (extract && mode == 15) {
        char retained[2048];
        if (snprintf(retained, sizeof retained, "%s.old", source) >= (int)sizeof retained || rename(source, retained) != 0) return 97;
        int previous = open(retained, O_RDONLY), replacement = open(source, O_WRONLY | O_CREAT | O_EXCL, 0600);
        unsigned char copied[4096]; off_t offset = 0;
        if (previous < 0 || replacement < 0) return 98;
        for (;;) {
            ssize_t count = pread(previous, copied, sizeof copied, offset);
            if (count < 0 && errno == EINTR) continue;
            if (count < 0) return 98;
            if (count == 0) break;
            if (offset == 0 && count > 256) copied[256] ^= 1;
            if (write_all(replacement, copied, (size_t)count) != 0) return 98;
            offset += count;
        }
        if (close(previous) != 0 || close(replacement) != 0) return 98;
    }
    if (mode == 12) return symlink(source, output) == 0 ? 0 : 87;
    if (mode == 13) return mkfifo(output, 0600) == 0 ? 0 : 95;
    int encoded = open(output, O_WRONLY | O_CREAT | O_EXCL, 0600);
    if (encoded < 0) return 88;
    if (mode == 2) return close(encoded) == 0 ? 0 : 89;
    if (mode == 8) {
        unsigned char bytes[4096] = {0}; uint64_t remaining = extract ? logical_bytes + 1 : 8193;
        while (remaining != 0) {
            size_t n = remaining < sizeof bytes ? (size_t)remaining : sizeof bytes;
            if (write_all(encoded, bytes, n) != 0) { close(encoded); return 91; }
            remaining -= n;
        }
        close(encoded); return 0;
    }
    if (extract) {
        uint64_t length_to_write = mode == 3 ? logical_bytes - 2048 : mode == 4 ? logical_bytes + 2048 : logical_bytes;
        unsigned char raw[4096]; uint64_t offset = 0;
        while (offset < length_to_write) {
            size_t count = length_to_write - offset < sizeof raw ? (size_t)(length_to_write - offset) : sizeof raw;
            for (size_t i = 0; i < count; ++i) raw[i] = (unsigned char)((offset + i) % 251);
            if (write_all(encoded, raw, count) != 0) { close(encoded); return 91; }
            offset += count;
        }
        if (close(encoded) != 0) return 92;
        if (write_all(STDOUT_FILENO, report, (size_t)length) != 0) return 93;
        if (mode == 14 && write_all(STDERR_FILENO, "stderr-tail-marker", 18) != 0) return 94;
        return 0;
    }
    unsigned char bytes[1340] = {0};
    memcpy(bytes, mode == 3 ? "BadCHD!!" : "MComprHD", 8);
    be32(bytes + 8, 124); be32(bytes + 12, 5); be32(bytes + 16, UINT32_C(0x7a737464));
    be64(bytes + 32, (uint64_t)original.st_size + (mode == 4 ? 2048 : 0));
    be32(bytes + 56, 16384); be32(bytes + 60, 2048);
    if (write_all(encoded, bytes, sizeof bytes) != 0 || close(encoded) != 0) return 92;
    if (write_all(STDOUT_FILENO, report, (size_t)length) != 0) return 93;
    if (mode == 14 && write_all(STDERR_FILENO, "stderr-tail-marker", 18) != 0) return 94;
    return 0;
}
