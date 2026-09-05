/* seekoracle: ground-truth seekable-zstd writer/reader for parity tests.
 *
 * Uses the real libzstd 1.5.7 streaming API with the framing policy of
 * zeekstd 0.4.5 (`RawEncoder` / `Encoder` in lib/src/encode.rs, driven with
 * the CLI's 128 KiB reads in cli/src/compress.rs). Gives byte-exact oracle
 * files for `Uncompressed` / `Compressed` frame policies, checksums on/off,
 * and `Foot` / standalone-`Head` seek tables.
 *
 * Out of scope (not ported in Step 4): prefix / patch-from support.
 *
 * Usage:
 *   seekoracle info
 *   seekoracle enc <in> <out> [--level N] [--usize N | --csize N]
 *       [--checksum | --no-checksum] [--foot | --head <tablefile>]
 *   seekoracle dec <in> <out> [--from N] [--to N | --to end]
 *       [--table <file> [--format head | --format foot]]
 */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "zstd.h"

#define SEEKABLE_MAGIC UINT32_C(0x8F92EAB1)
#define SKIPPABLE_MAGIC UINT32_C(0x184D2A5E)
#define SEEKABLE_MAX_FRAMES UINT32_C(0x08000000)
#define SEEKABLE_MAX_FRAME_SIZE ((size_t)0x40000000)
#define SEEK_TABLE_INTEGRITY_SIZE 9
#define SKIPPABLE_HEADER_SIZE 8
#define CLI_READ_SIZE 131072 /* CCtx::in_size() == ZSTD_CStreamInSize() */

static void die(const char *msg)
{
    fprintf(stderr, "seekoracle: %s\n", msg);
    exit(1);
}

static void check_zstd(size_t code, const char *what)
{
    if (ZSTD_isError(code)) {
        fprintf(stderr, "seekoracle: %s: %s\n", what, ZSTD_getErrorName(code));
        exit(1);
    }
}

static void write_le32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)v;
    p[1] = (uint8_t)(v >> 8);
    p[2] = (uint8_t)(v >> 16);
    p[3] = (uint8_t)(v >> 24);
}

static uint32_t read_le32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16)
        | ((uint32_t)p[3] << 24);
}

/* ------------------------------------------------------------------ */
/* Encoder state (mirrors zeekstd RawEncoder + std Encoder).           */
/* ------------------------------------------------------------------ */

typedef struct {
    ZSTD_CCtx *cctx;
    int policy_compressed; /* 0 = Uncompressed, 1 = Compressed */
    uint32_t policy_size;
    uint64_t frame_c;
    uint64_t frame_d;
    uint32_t *tab_c;
    uint32_t *tab_d;
    size_t num_frames;
    size_t tab_cap;
    uint8_t *out;
    size_t out_cap;
    size_t opos;
    FILE *fout;
} Enc;

static size_t eff_limit(const Enc *e)
{
    size_t lim = e->policy_size;
    if (lim > SEEKABLE_MAX_FRAME_SIZE)
        lim = SEEKABLE_MAX_FRAME_SIZE;
    return lim;
}

/* zeekstd RawEncoder::is_frame_complete */
static int is_frame_complete(const Enc *e)
{
    if (e->policy_compressed)
        return e->policy_size <= e->frame_c || SEEKABLE_MAX_FRAME_SIZE <= e->frame_d;
    return eff_limit(e) <= e->frame_d;
}

/* zeekstd RawEncoder::remaining_frame_size */
static size_t remaining_frame_size(const Enc *e)
{
    if (e->policy_compressed)
        return SEEKABLE_MAX_FRAME_SIZE - (size_t)e->frame_d;
    return eff_limit(e) - (size_t)e->frame_d;
}

static void log_frame(Enc *e)
{
    if (e->num_frames >= SEEKABLE_MAX_FRAMES)
        die("too many frames");
    if (e->frame_c > UINT32_MAX || e->frame_d > UINT32_MAX)
        die("frame size overflows u32");
    if (e->num_frames == e->tab_cap) {
        e->tab_cap = e->tab_cap ? e->tab_cap * 2 : 16;
        e->tab_c = realloc(e->tab_c, e->tab_cap * sizeof(uint32_t));
        e->tab_d = realloc(e->tab_d, e->tab_cap * sizeof(uint32_t));
        if (!e->tab_c || !e->tab_d)
            die("out of memory");
    }
    e->tab_c[e->num_frames] = (uint32_t)e->frame_c;
    e->tab_d[e->num_frames] = (uint32_t)e->frame_d;
    e->num_frames++;
    {
        size_t r = ZSTD_CCtx_reset(e->cctx, ZSTD_reset_session_only);
        check_zstd(r, "reset session");
    }
    e->frame_c = 0;
    e->frame_d = 0;
}

static void flush_out(Enc *e, int force)
{
    if (e->opos == e->out_cap || force) {
        if (e->opos > 0) {
            if (fwrite(e->out, 1, e->opos, e->fout) != e->opos)
                die("write failed");
            e->opos = 0;
        }
    }
}

/* One RawEncoder::end_frame call over out[e->opos .. cap).
 * Sets *produced and *done (done=1 means epilogue finished and the frame
 * was logged). Mirrors the check-first ordering (n==0 before full check). */
static void raw_end_frame_into(Enc *e, size_t *produced, int *done)
{
    static const uint8_t empty_in[1] = { 0 };
    size_t pos = 0;
    for (;;) {
        ZSTD_inBuffer in = { empty_in, 0, 0 };
        ZSTD_outBuffer out = { e->out + e->opos + pos, e->out_cap - e->opos - pos, 0 };
        size_t prev = out.pos;
        size_t r = ZSTD_compressStream2(e->cctx, &out, &in, ZSTD_e_end);
        check_zstd(r, "end frame");
        e->frame_c += (uint64_t)(out.pos - prev);
        pos = out.pos;
        if (r == 0) {
            log_frame(e);
            *produced = pos;
            *done = 1;
            return;
        }
        if (e->opos + pos == e->out_cap) {
            *produced = pos;
            *done = 0;
            return;
        }
    }
}

/* Encoder::end_frame (std): loop raw end + flush(false) until done. */
static void encoder_end_frame(Enc *e)
{
    for (;;) {
        size_t p;
        int done;
        raw_end_frame_into(e, &p, &done);
        e->opos += p;
        flush_out(e, 0);
        if (done)
            return;
    }
}

/* One RawEncoder::compress_with_prefix call (no prefix). Returns input
 * consumed; advances e->opos. Mirrors the complete-branch loop and the
 * e_continue loop exactly. */
static size_t raw_compress(Enc *e, const uint8_t *ptr, size_t len)
{
    if (is_frame_complete(e)) {
        size_t oprog = 0;
        while (oprog < e->out_cap - e->opos) {
            size_t p;
            int done;
            raw_end_frame_into(e, &p, &done);
            oprog += p;
            if (done)
                break;
        }
        e->opos += oprog;
        return 0;
    }
    {
        size_t limit = len < remaining_frame_size(e) ? len : remaining_frame_size(e);
        ZSTD_inBuffer in = { ptr, limit, 0 };
        ZSTD_outBuffer out = { e->out + e->opos, e->out_cap - e->opos, 0 };
        while (in.pos < limit && out.pos < out.size) {
            size_t r = ZSTD_compressStream2(e->cctx, &out, &in, ZSTD_e_continue);
            check_zstd(r, "compress");
        }
        e->frame_c += (uint64_t)out.pos;
        e->frame_d += (uint64_t)in.pos;
        e->opos += out.pos;
        return in.pos;
    }
}

/* Encoder::compress_with_prefix over one CLI-sized read. */
static void encoder_compress_read(Enc *e, const uint8_t *ptr, size_t len)
{
    size_t prog = 0;
    while (prog < len) {
        size_t before = e->opos;
        size_t n = raw_compress(e, ptr + prog, len - prog);
        flush_out(e, 0);
        if (n == 0 && e->opos == before) {
            /* Progress is guaranteed by the flush-if-full invariant (see
             * Encoder::compress); reaching here means a modeling bug. */
            die("compress made no progress");
        }
        prog += n;
    }
}

static void write_seek_table(Enc *e, FILE *f, int head)
{
    uint8_t hdr[SKIPPABLE_HEADER_SIZE + SEEK_TABLE_INTEGRITY_SIZE];
    uint32_t frame_size = (uint32_t)(e->num_frames * 8 + SEEK_TABLE_INTEGRITY_SIZE);
    write_le32(hdr, SKIPPABLE_MAGIC);
    write_le32(hdr + 4, frame_size);
    write_le32(hdr + 8, (uint32_t)e->num_frames);
    hdr[12] = 0; /* descriptor: checksum flag 0, like zeekstd */
    write_le32(hdr + 13, SEEKABLE_MAGIC);
    if (head) {
        if (fwrite(hdr, 1, sizeof hdr, f) != sizeof hdr)
            die("write failed");
    } else {
        if (fwrite(hdr, 1, SKIPPABLE_HEADER_SIZE, f) != SKIPPABLE_HEADER_SIZE)
            die("write failed");
    }
    for (size_t i = 0; i < e->num_frames; i++) {
        uint8_t ent[8];
        write_le32(ent, e->tab_c[i]);
        write_le32(ent + 4, e->tab_d[i]);
        if (fwrite(ent, 1, 8, f) != 8)
            die("write failed");
    }
    if (!head) {
        if (fwrite(hdr + SKIPPABLE_HEADER_SIZE, 1, SEEK_TABLE_INTEGRITY_SIZE, f)
            != SEEK_TABLE_INTEGRITY_SIZE)
            die("write failed");
    }
}

static uint64_t parse_u64(const char *s, const char *what)
{
    char *end;
    unsigned long long v = strtoull(s, &end, 10);
    if (end == s || *end != '\0')
        die(what);
    return (uint64_t)v;
}

static int do_enc(int argc, char **argv)
{
    const char *in_path = NULL, *out_path = NULL, *head_path = NULL;
    int level = 3;
    int policy_compressed = 0;
    uint64_t policy_size = 2u * 1024u * 1024u;
    int checksum = 1;
    int head = 0;
    for (int i = 0; i < argc; i++) {
        if (!strcmp(argv[i], "--level") && i + 1 < argc)
            level = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--usize") && i + 1 < argc) {
            policy_compressed = 0;
            policy_size = parse_u64(argv[++i], "bad --usize");
        } else if (!strcmp(argv[i], "--csize") && i + 1 < argc) {
            policy_compressed = 1;
            policy_size = parse_u64(argv[++i], "bad --csize");
        } else if (!strcmp(argv[i], "--checksum"))
            checksum = 1;
        else if (!strcmp(argv[i], "--no-checksum"))
            checksum = 0;
        else if (!strcmp(argv[i], "--foot"))
            head = 0;
        else if (!strcmp(argv[i], "--head") && i + 1 < argc) {
            head = 1;
            head_path = argv[++i];
        } else if (!in_path)
            in_path = argv[i];
        else if (!out_path)
            out_path = argv[i];
        else
            die("usage: enc <in> <out> [options]");
    }
    if (!in_path || !out_path || (head && !head_path))
        die("usage: enc <in> <out> [options]");
    if (policy_size < 1 || policy_size > UINT32_MAX)
        die("frame size out of range");

    FILE *fin = fopen(in_path, "rb");
    if (!fin)
        die("cannot open input");
    FILE *fout = fopen(out_path, "wb");
    if (!fout)
        die("cannot open output");

    Enc e;
    memset(&e, 0, sizeof e);
    e.cctx = ZSTD_createCCtx();
    if (!e.cctx)
        die("cannot create cctx");
    check_zstd(ZSTD_CCtx_setParameter(e.cctx, ZSTD_c_compressionLevel, level), "set level");
    check_zstd(
        ZSTD_CCtx_setParameter(e.cctx, ZSTD_c_checksumFlag, checksum ? 1 : 0),
        "set checksum");
    e.policy_compressed = policy_compressed;
    e.policy_size = (uint32_t)policy_size;
    e.out_cap = ZSTD_CStreamOutSize();
    e.out = malloc(e.out_cap);
    if (!e.out)
        die("out of memory");
    e.fout = fout;

    {
        uint8_t *buf = malloc(CLI_READ_SIZE);
        size_t n;
        if (!buf)
            die("out of memory");
        while ((n = fread(buf, 1, CLI_READ_SIZE, fin)) > 0)
            encoder_compress_read(&e, buf, n);
        if (ferror(fin))
            die("read failed");
        free(buf);
    }
    encoder_end_frame(&e);
    flush_out(&e, 1);

    if (head) {
        FILE *fh = fopen(head_path, "wb");
        if (!fh)
            die("cannot open table output");
        write_seek_table(&e, fh, 1);
        fclose(fh);
    } else {
        write_seek_table(&e, fout, 0);
    }

    fclose(fin);
    fclose(fout);
    ZSTD_freeCCtx(e.cctx);
    free(e.out);
    free(e.tab_c);
    free(e.tab_d);
    return 0;
}

/* ------------------------------------------------------------------ */
/* Decoder: parse Foot/Head/legacy tables, dump [from, to).            */
/* ------------------------------------------------------------------ */

typedef struct {
    uint64_t *c_off;
    uint64_t *d_off;
    uint32_t num_frames;
} Table;

static uint8_t *read_all(const char *path, size_t *len_out)
{
    FILE *f = fopen(path, "rb");
    size_t cap = 1 << 16, len = 0;
    uint8_t *buf;
    size_t n;
    if (!f)
        die("cannot open file");
    buf = malloc(cap);
    if (!buf)
        die("out of memory");
    while ((n = fread(buf + len, 1, cap - len, f)) > 0) {
        len += n;
        if (len == cap) {
            cap *= 2;
            buf = realloc(buf, cap);
            if (!buf)
                die("out of memory");
        }
    }
    if (ferror(f))
        die("read failed");
    fclose(f);
    *len_out = len;
    return buf;
}

/* Parse integrity field at p[0..9) (num_frames, descriptor, magic). */
static void parse_integrity(
    const uint8_t *p, uint32_t *num_frames, size_t *size_per_frame)
{
    if (read_le32(p + 5) != SEEKABLE_MAGIC)
        die("bad seekable magic (not a seek table)");
    if (((p[4] >> 2) & 0x1f) != 0)
        die("reserved descriptor bits set");
    *num_frames = read_le32(p);
    if (*num_frames > SEEKABLE_MAX_FRAMES)
        die("frame index too large");
    *size_per_frame = (p[4] & 0x80) ? 12 : 8; /* legacy checksums ignored */
}

/* Parse a Foot table from the tail of buf[0..len). */
static Table parse_foot(const uint8_t *buf, size_t len)
{
    Table t = { 0, 0, 0 };
    uint32_t n;
    size_t epf, table_size, start;
    size_t i;
    if (len < SEEK_TABLE_INTEGRITY_SIZE)
        die("file too short for seek table");
    parse_integrity(buf + len - SEEK_TABLE_INTEGRITY_SIZE, &n, &epf);
    table_size = (size_t)n * epf + SKIPPABLE_HEADER_SIZE + SEEK_TABLE_INTEGRITY_SIZE;
    if (table_size > len)
        die("truncated seek table");
    start = len - table_size;
    if (read_le32(buf + start) != SKIPPABLE_MAGIC)
        die("bad skippable magic");
    if ((size_t)read_le32(buf + start + 4) + SKIPPABLE_HEADER_SIZE != table_size)
        die("seek table size mismatch");
    t.c_off = malloc(((size_t)n + 1) * sizeof(uint64_t));
    t.d_off = malloc(((size_t)n + 1) * sizeof(uint64_t));
    if ((n && (!t.c_off || !t.d_off)))
        die("out of memory");
    {
        uint64_t co = 0, doff = 0;
        const uint8_t *p = buf + start + SKIPPABLE_HEADER_SIZE;
        t.c_off[0] = 0;
        t.d_off[0] = 0;
        for (i = 0; i < n; i++) {
            co += read_le32(p);
            doff += read_le32(p + 4);
            t.c_off[i + 1] = co;
            t.d_off[i + 1] = doff;
            p += epf;
        }
    }
    t.num_frames = n;
    return t;
}

/* Parse a standalone Head table in buf[0..len). */
static Table parse_head(const uint8_t *buf, size_t len)
{
    Table t = { 0, 0, 0 };
    uint32_t n;
    size_t epf, table_size, i;
    if (len < SKIPPABLE_HEADER_SIZE + SEEK_TABLE_INTEGRITY_SIZE)
        die("file too short for seek table");
    if (read_le32(buf) != SKIPPABLE_MAGIC)
        die("bad skippable magic");
    parse_integrity(buf + SKIPPABLE_HEADER_SIZE, &n, &epf);
    table_size = (size_t)n * epf + SKIPPABLE_HEADER_SIZE + SEEK_TABLE_INTEGRITY_SIZE;
    if ((size_t)read_le32(buf + 4) + SKIPPABLE_HEADER_SIZE != table_size)
        die("seek table size mismatch");
    if (len < table_size)
        die("truncated seek table");
    t.c_off = malloc(((size_t)n + 1) * sizeof(uint64_t));
    t.d_off = malloc(((size_t)n + 1) * sizeof(uint64_t));
    if ((n && (!t.c_off || !t.d_off)))
        die("out of memory");
    {
        uint64_t co = 0, doff = 0;
        const uint8_t *p = buf + SKIPPABLE_HEADER_SIZE + SEEK_TABLE_INTEGRITY_SIZE;
        t.c_off[0] = 0;
        t.d_off[0] = 0;
        for (i = 0; i < n; i++) {
            co += read_le32(p);
            doff += read_le32(p + 4);
            t.c_off[i + 1] = co;
            t.d_off[i + 1] = doff;
            p += epf;
        }
    }
    t.num_frames = n;
    return t;
}

/* Binary search mirror of zeekstd frame_index_at (offsets arrays hold
 * num_frames+1 cumulative entries). */
static uint32_t frame_index_at(const uint64_t *starts, uint32_t num_frames, uint64_t offset)
{
    uint32_t low = 0, high = num_frames;
    if (num_frames == 0)
        die("empty seek table");
    if (offset >= starts[num_frames])
        return num_frames - 1;
    while (low + 1 < high) {
        uint32_t mid = low + (high - low) / 2;
        if (starts[mid] <= offset)
            low = mid;
        else
            high = mid;
    }
    return low;
}

static int do_dec(int argc, char **argv)
{
    const char *in_path = NULL, *out_path = NULL, *table_path = NULL;
    uint64_t from = 0, to = UINT64_MAX;
    int table_format_head = -1; /* -1 = embedded foot */
    for (int i = 0; i < argc; i++) {
        if (!strcmp(argv[i], "--from") && i + 1 < argc)
            from = parse_u64(argv[++i], "bad --from");
        else if (!strcmp(argv[i], "--to") && i + 1 < argc) {
            if (!strcmp(argv[i + 1], "end")) {
                i++;
                to = UINT64_MAX;
            } else
                to = parse_u64(argv[++i], "bad --to");
        } else if (!strcmp(argv[i], "--table") && i + 1 < argc)
            table_path = argv[++i];
        else if (!strcmp(argv[i], "--format") && i + 1 < argc) {
            ++i;
            if (!strcmp(argv[i], "head"))
                table_format_head = 1;
            else if (!strcmp(argv[i], "foot"))
                table_format_head = 0;
            else
                die("bad --format");
        } else if (!in_path)
            in_path = argv[i];
        else if (!out_path)
            out_path = argv[i];
        else
            die("usage: dec <in> <out> [options]");
    }
    if (!in_path || !out_path)
        die("usage: dec <in> <out> [options]");
    if (table_format_head >= 0 && !table_path)
        die("--format needs --table");

    {
        size_t data_len, tab_len = 0;
        uint8_t *data = read_all(in_path, &data_len);
        Table t;
        uint64_t total;
        FILE *fout;
        uint32_t first, last, f;
        if (table_path) {
            uint8_t *tab = read_all(table_path, &tab_len);
            t = table_format_head ? parse_head(tab, tab_len) : parse_foot(tab, tab_len);
            free(tab);
        } else {
            t = parse_foot(data, data_len);
        }
        total = t.d_off[t.num_frames];
        if (to > total)
            to = total;
        if (from > to)
            die("from > to");
        fout = fopen(out_path, "wb");
        if (!fout)
            die("cannot open output");
        if (from < to) {
            first = frame_index_at(t.d_off, t.num_frames, from);
            last = frame_index_at(t.d_off, t.num_frames, to - 1);
            for (f = first; f <= last; f++) {
                uint64_t cs = t.c_off[f], ce = t.c_off[f + 1];
                uint64_t ds = t.d_off[f], de = t.d_off[f + 1];
                size_t csize = (size_t)(ce - cs), dsize = (size_t)(de - ds);
                uint8_t *dec;
                size_t r;
                uint64_t lo, hi;
                if (ce > data_len)
                    die("frame extends past input");
                dec = malloc(dsize ? dsize : 1);
                if (!dec)
                    die("out of memory");
                r = ZSTD_decompress(dec, dsize, data + cs, csize);
                if (ZSTD_isError(r)) {
                    fprintf(stderr, "seekoracle: frame %u: %s\n", f, ZSTD_getErrorName(r));
                    exit(1);
                }
                if (r != dsize) {
                    fprintf(stderr, "seekoracle: frame %u size mismatch\n", f);
                    exit(1);
                }
                lo = from > ds ? from - ds : 0;
                hi = to < de ? to - ds : de - ds;
                if (hi > lo) {
                    if (fwrite(dec + lo, 1, (size_t)(hi - lo), fout) != (size_t)(hi - lo))
                        die("write failed");
                }
                free(dec);
            }
        }
        fclose(fout);
        free(data);
        free(t.c_off);
        free(t.d_off);
    }
    return 0;
}

int main(int argc, char **argv)
{
    if (argc >= 2 && !strcmp(argv[1], "info")) {
        printf("in=%zu out=%zu\n", ZSTD_CStreamInSize(), ZSTD_CStreamOutSize());
        return 0;
    }
    if (argc >= 2 && !strcmp(argv[1], "enc"))
        return do_enc(argc - 2, argv + 2);
    if (argc >= 2 && !strcmp(argv[1], "dec"))
        return do_dec(argc - 2, argv + 2);
    fprintf(stderr, "usage: seekoracle <info|enc|dec> ...\n");
    return 1;
}
