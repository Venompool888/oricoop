#!/usr/bin/env python3
"""Merge Ori Co-op traces / BepInEx logs from both ends onto one timeline.

Usage:
  python -I tools\\merge-coop-logs.py P1.log P2.log
  python -I tools\\merge-coop-logs.py p1-host-LogOutput.log p2-guest-LogOutput.log --summary
  python -I tools\\merge-coop-logs.py trace1.log trace2.log --diff --window 2
  python -I tools\\merge-coop-logs.py old1.log@P1 old2.log@P2 --anchor old1.log:120=19:40:02 --date 2026-11-16 --utc-offset +01:00
  python -I tools\\merge-coop-logs.py --selftest
"""
import argparse
import hashlib
import json
import os
import re
import shlex
import sys
import tempfile
from bisect import bisect_left
from datetime import date, datetime, time, timedelta, timezone

RATE = 0.5
IP_RE = re.compile(r'\b(?:\d{1,3}\.){3}\d{1,3}\b')
UTC_RE = re.compile(r'^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{3})Z$')
PREFIX_RE = re.compile(r'^\[[A-Za-z]+\s*:\s*([^\]]*)\]\s?(.*)$')
IDENT = ('id', 'obj', 'name', 'enemy')


class Event(object):
    def __init__(self, path, lineno, fmt, role, sid, subsys, name, fields,
                 local=None, utc=None, approx=False):
        self.path = path
        self.lineno = lineno
        self.fmt = fmt
        self.role = role
        self.sid = sid
        self.subsys = subsys
        self.name = name
        self.fields = fields
        self.local = local
        self.utc = utc
        self.approx = approx


class LogFile(object):
    def __init__(self, path, given, fmt):
        self.path = path
        self.given = given
        self.fmt = fmt
        self.role = '?'
        self.sid = '-'
        self.events = []
        self.lines = 0
        self.anchors = []
        self.header = {}

    @property
    def last_line(self):
        return max(1, self.lines)


def mask_ips(text):
    def repl(m):
        ip = m.group(0)
        if ip.startswith('127.'):
            return ip
        return 'ip#' + hashlib.sha1(ip.encode('utf-8')).hexdigest()[:4]
    return IP_RE.sub(repl, str(text))


def parse_clock(s):
    parts = s.strip().split(':')
    if len(parts) != 3:
        raise ValueError('bad time: ' + s)
    h, m = int(parts[0]), int(parts[1])
    sec = float(parts[2])
    whole = int(sec)
    us = int(round((sec - whole) * 1000000))
    if us == 1000000:
        whole += 1
        us = 0
    return time(h, m, whole, us)


def parse_offset(s):
    s = s.strip()
    sign = -1 if s.startswith('-') else 1
    s = s.lstrip('+-')
    h, m = s.split(':')
    return sign * timedelta(hours=int(h), minutes=int(m))


def parse_utc_token(tok):
    m = UTC_RE.match(tok)
    if not m:
        try:
            return datetime.fromisoformat(tok.replace('Z', '+00:00'))
        except ValueError:
            return None
    y, mo, d, h, mi, se, ms = (int(x) for x in m.groups())
    return datetime(y, mo, d, h, mi, se, ms * 1000, tzinfo=timezone.utc)


def split_spec(spec):
    m = re.match(r'^(.*)@([A-Za-z][A-Za-z0-9_-]*)$', spec)
    if m and not m.group(1).endswith('\\'):
        return m.group(1), m.group(2)
    return spec, None


def guess_role(spec, path):
    if spec:
        return spec.upper()
    low = os.path.basename(path).lower()
    if re.search(r'(^|[^a-z0-9])p1([^0-9]|$)|host', low):
        return 'P1'
    if re.search(r'(^|[^a-z0-9])p2([^0-9]|$)|guest', low):
        return 'P2'
    return '?'


def fmt_fields(fields):
    out = []
    for k, v in fields.items():
        v = str(v)
        if v == '' or re.search(r'[\s"\\=]', v):
            out.append(k + '=' + json.dumps(v, ensure_ascii=False))
        else:
            out.append(k + '=' + v)
    return ' '.join(out)


def display_dt(e, args):
    if args.utc:
        if e.utc is not None:
            return e.utc.astimezone(timezone.utc).replace(tzinfo=None)
        return e.local - args.offset
    return e.local


def fmt_time(dt, approx):
    return dt.strftime('%H:%M:%S.%f')[:12] + ('~' if approx else '')


def fmt_event(e, args):
    f = fmt_fields(e.fields)
    line = '%s %s %s %s' % (fmt_time(display_dt(e, args), e.approx), e.role, e.subsys, e.name)
    return line + (' ' + f if f else '')


def old_parts(msg):
    text = msg.strip()
    low = text.lower()
    if text.startswith('net: E3 '):
        return 'enemy', first_word(text[len('net: E3 '):])
    if text.startswith('net: '):
        return 'net', first_word(text[5:])
    if text.startswith('E3 '):
        return 'enemy', first_word(text[3:])
    if text.startswith('online:'):
        return 'net', first_word(text.split(':', 1)[1])
    if low.startswith('world') or low.startswith('push'):
        return 'phys', first_word(text)
    if low.startswith('story') or low.startswith('cutscene'):
        return 'story', first_word(text)
    return 'log', first_word(text)


def first_word(s):
    m = re.match(r'[A-Za-z0-9_.-]+', s.strip())
    return m.group(0) if m else 'log'


def is_new_format(lines):
    for line in lines:
        if not line.strip():
            continue
        if line.startswith('#') and 'started_utc=' in line:
            return True
        if re.match(r'^\d{2}:\d{2}:\d{2}\.\d{3} \d{4}-\d{2}-\d{2}T', line):
            return True
        return False
    return False


def parse_new(f, lines, args):
    offset = args.offset
    for line in lines:
        if line.startswith('#'):
            for k, v in re.findall(r'(\w+)=(\S+)', line):
                f.header[k] = v
            continue
    if 'utc_offset_min' in f.header:
        try:
            offset = timedelta(minutes=int(f.header['utc_offset_min']))
        except ValueError:
            pass
    for lineno, line in enumerate(lines, 1):
        line = line.rstrip('\r\n')
        if not line.strip() or line.startswith('#'):
            continue
        try:
            toks = shlex.split(line, comments=False, posix=True)
        except ValueError:
            continue
        if len(toks) < 9:
            continue
        utc = parse_utc_token(toks[1])
        if utc is None:
            continue
        i = 2
        fixed = {}
        while i < len(toks) and '=' in toks[i]:
            k, v = toks[i].split('=', 1)
            fixed[k] = v
            i += 1
        if i + 1 >= len(toks):
            continue
        subsys, name = toks[i], toks[i + 1]
        i += 2
        fields = {}
        while i < len(toks):
            if '=' in toks[i]:
                k, v = toks[i].split('=', 1)
                fields[k] = v
            i += 1
        local = (utc + offset).replace(tzinfo=None)
        e = Event(f.path, lineno, 'new', fixed.get('role', f.role),
                  fixed.get('sid', '-'), subsys, name, fields, local, utc)
        f.events.append(e)
        if e.role != '?':
            f.role = e.role
        if e.sid != '-':
            f.sid = e.sid


def parse_old(f, lines, args):
    for lineno, line in enumerate(lines, 1):
        raw = line.rstrip('\r\n')
        if not raw.strip():
            continue
        m = PREFIX_RE.match(raw)
        msg = m.group(2) if m else raw
        subsys, name = old_parts(msg)
        f.events.append(Event(f.path, lineno, 'old', f.role, '-', subsys, name,
                              {'msg': msg}, approx=True))


def load_files(args):
    files = []
    for spec_arg in args.files:
        path, role = split_spec(spec_arg)
        with open(path, 'r', encoding='utf-8-sig', errors='replace') as fh:
            lines = fh.readlines()
        fmt = 'new' if is_new_format(lines) else 'old'
        f = LogFile(path, spec_arg, fmt)
        f.lines = len(lines)
        f.role = guess_role(role, path)
        if fmt == 'new':
            parse_new(f, lines, args)
        else:
            parse_old(f, lines, args)
        files.append(f)
    return files


def find_file(files, spec):
    for f in files:
        if spec == f.given or spec == f.path:
            return f
    base = os.path.basename(spec).lower()
    for f in files:
        if os.path.basename(f.path).lower() == base:
            return f
    try:
        ap = os.path.abspath(spec)
        for f in files:
            if os.path.abspath(f.path) == ap:
                return f
    except OSError:
        pass
    return None


def add_cli_anchors(files, args):
    for val in args.anchor or []:
        left, clock = val.rsplit('=', 1)
        spec, line = left.rsplit(':', 1)
        f = find_file(files, spec)
        if f:
            f.anchors.append((int(line), datetime.combine(args.date, parse_clock(clock))))
    for val in args.start or []:
        spec, clock = val.rsplit('=', 1)
        f = find_file(files, spec)
        if f:
            f.anchors.append((1, datetime.combine(args.date, parse_clock(clock))))
    for val in args.end or []:
        spec, clock = val.rsplit('=', 1)
        f = find_file(files, spec)
        if f:
            f.anchors.append((f.last_line, datetime.combine(args.date, parse_clock(clock))))


def estimate_time(anchors, line, base):
    if not anchors:
        return base + timedelta(seconds=RATE * max(0, line - 1))
    a = sorted(anchors)
    dedup = []
    for ln, dt in a:
        if dedup and dedup[-1][0] == ln:
            dedup[-1] = (ln, dt)
        else:
            dedup.append((ln, dt))
    a = dedup
    if len(a) == 1:
        return a[0][1] + timedelta(seconds=RATE * (line - a[0][0]))
    if line <= a[0][0]:
        p, q = a[0], a[1]
    elif line >= a[-1][0]:
        p, q = a[-2], a[-1]
    else:
        p, q = a[0], a[1]
        for i in range(len(a) - 1):
            if a[i][0] <= line <= a[i + 1][0]:
                p, q = a[i], a[i + 1]
                break
    if q[0] == p[0]:
        return p[1]
    frac = (line - p[0]) / float(q[0] - p[0])
    return p[1] + (q[1] - p[1]) * frac


def auto_anchor_keys(e):
    if e.fmt != 'old':
        return []
    msg = e.fields.get('msg', '')
    keys = []
    if 'net: connected to ' in msg:
        keys.append(('connected', ''))
    m = re.search(r'net: partner runs (\S+)', msg)
    if m:
        keys.append(('version', m.group(1)))
    if "net: partner's Ori appears" in msg:
        keys.append(('orients', ''))
    m = re.search(r'net: (?:sent|received) checkpoint (\d+) bytes', msg)
    if m:
        keys.append(('checkpoint', m.group(1)))
    m = re.search(r'net: E3 (damage|died|remove)\b.*?\benemy=(\S+)', msg)
    if m:
        keys.append(('e3:' + m.group(1), m.group(2)))
    return keys


def align_old_files(files, args, base):
    olds = [f for f in files if f.fmt == 'old']
    if len(olds) < 2:
        return
    ref = next((f for f in olds if f.role == 'P1'), olds[0])
    others = [f for f in olds if f is not ref]
    maps = {}
    for f in olds:
        d = {}
        for e in f.events:
            for key in auto_anchor_keys(e):
                d.setdefault(key, []).append(e.lineno)
        maps[f] = d
    cands = []
    for key, lines in maps[ref].items():
        if any(key not in maps[f] for f in others):
            continue
        n = min([len(lines)] + [len(maps[f][key]) for f in others])
        for i in range(n):
            cands.append((lines[i], key, [maps[f][key][i] for f in others]))
    cands.sort(key=lambda x: x[0])
    for ref_line, key, other_lines in cands:
        t = estimate_time(ref.anchors, ref_line, base)
        ref.anchors.append((ref_line, t))
        for f, line in zip(others, other_lines):
            f.anchors.append((line, t))


def estimate_old(files, args, base):
    for f in files:
        if f.fmt != 'old':
            continue
        for e in f.events:
            e.local = estimate_time(f.anchors, e.lineno, base)
            e.utc = (e.local - args.offset).replace(tzinfo=timezone.utc)
            e.approx = True


def selected(events, args):
    subs = set(x.strip() for x in args.subsys.split(',') if x.strip()) if args.subsys else None
    roles = set(x.strip().upper() for x in args.role.split(',') if x.strip()) if args.role else None
    t_from = parse_clock(args.from_) if args.from_ else None
    t_to = parse_clock(args.to) if args.to else None
    out = []
    for e in events:
        if subs and e.subsys not in subs:
            continue
        if roles and e.role.upper() not in roles:
            continue
        if args.sid and e.sid != args.sid:
            continue
        if e.local is None:
            continue
        dt = display_dt(e, args)
        tod = dt.time()
        if t_from and tod < t_from:
            continue
        if t_to and tod > t_to:
            continue
        if args.grep:
            hay = e.subsys + ' ' + e.name + ' ' + fmt_fields(e.fields)
            if not re.search(args.grep, hay):
                continue
        out.append(e)
    return out


def new_ident(e):
    for k in IDENT:
        if k in e.fields:
            return k, e.fields[k]
    return None


def new_key(e):
    if e.subsys == 'log':
        return None
    ident = new_ident(e)
    if ident is None:
        return None
    return (e.subsys, e.name, ident[1])


def old_key(e):
    msg = IP_RE.sub('', e.fields.get('msg', ''))
    msg = re.sub(r'\d+', '#', msg)
    return ('old', e.subsys, msg)


def event_brief(e, args):
    if e.fmt == 'old':
        f = fmt_fields(e.fields)
        return '%s %s %s%s' % (fmt_time(display_dt(e, args), True), e.subsys, e.name,
                               (' ' + f) if f else '')
    key = new_ident(e)
    ident = ''
    if key:
        ident = ' %s=%s' % (key[0], key[1])
    return '%s %s %s%s' % (fmt_time(display_dt(e, args), e.approx), e.subsys, e.name, ident)


def nearest(lst, dt):
    if len(lst) == 1:
        return 0
    i = bisect_left(lst, dt)
    if i == 0:
        return 0
    if i >= len(lst):
        return len(lst) - 1
    return i if (lst[i] - dt) < (dt - lst[i - 1]) else i - 1


def build_diff(events, args):
    p1, p2 = [], []
    for e in events:
        key = old_key(e) if e.fmt == 'old' else new_key(e)
        if key is None or e.role not in ('P1', 'P2'):
            continue
        (p1 if e.role == 'P1' else p2).append((e, key))
    b_by = {}
    for e, key in p2:
        b_by.setdefault(key, []).append(e)
    for lst in b_by.values():
        lst.sort(key=lambda x: display_dt(x, args))
    a_by = {}
    for e, key in p1:
        a_by.setdefault(key, []).append(e)
    for lst in a_by.values():
        lst.sort(key=lambda x: display_dt(x, args))
    out = []
    seen = set()
    for e, key in p1:
        lst = b_by.get(key)
        if not lst:
            out.append((display_dt(e, args), 'ONLY P1: ' + event_brief(e, args)))
            continue
        times = [display_dt(x, args) for x in lst]
        j = nearest(times, display_dt(e, args))
        delta = abs((display_dt(e, args) - times[j]).total_seconds())
        if delta > args.window:
            pair = (id(e), id(lst[j]))
            if pair not in seen:
                seen.add(pair)
                out.append((min(display_dt(e, args), times[j]),
                            'DELTA %.3fs P1 %s / P2 %s' % (
                                delta, event_brief(e, args), event_brief(lst[j], args))))
    for e, key in p2:
        if key not in a_by:
            out.append((display_dt(e, args), 'ONLY P2: ' + event_brief(e, args)))
    out.sort(key=lambda x: x[0])
    return [x[1] for x in out]


def build_summary(files):
    out = []
    for f in files:
        times = [display_dt(e, ARGS) for e in f.events if e.local is not None]
        if times:
            rng = '%s~%s' % (fmt_time(min(times), f.fmt == 'old'),
                             fmt_time(max(times), f.fmt == 'old'))
        else:
            rng = '-'
        counts = {}
        for e in f.events:
            counts[e.subsys] = counts.get(e.subsys, 0) + 1
        subs = ','.join('%s:%d' % (k, counts[k]) for k in sorted(counts))
        out.append('%s: format=%s lines=%d range=%s sid=%s role=%s subsys=%s' % (
            f.path, f.fmt, f.lines, rng, f.sid, f.role, subs))
    return out


def run(argv):
    global ARGS
    ap = argparse.ArgumentParser(description='Merge Ori Co-op P1/P2 logs onto one timeline.')
    ap.add_argument('files', nargs='*', help='trace/log files, optionally FILE@P1 / FILE@P2')
    ap.add_argument('--anchor', action='append', metavar='FILE:LINE=HH:MM:SS')
    ap.add_argument('--start', action='append', metavar='FILE=HH:MM:SS')
    ap.add_argument('--end', action='append', metavar='FILE=HH:MM:SS')
    ap.add_argument('--date', metavar='YYYY-MM-DD')
    ap.add_argument('--utc-offset', default='+00:00')
    ap.add_argument('--utc', action='store_true')
    ap.add_argument('--subsys')
    ap.add_argument('--from', dest='from_', metavar='HH:MM:SS')
    ap.add_argument('--to', metavar='HH:MM:SS')
    ap.add_argument('--grep')
    ap.add_argument('--sid')
    ap.add_argument('--role')
    ap.add_argument('--diff', action='store_true')
    ap.add_argument('--window', type=float, default=2.0)
    ap.add_argument('--summary', action='store_true')
    ap.add_argument('--selftest', action='store_true')
    args = ap.parse_args(argv)
    ARGS = args
    if args.selftest:
        selftest()
        return ['SELFTEST OK']
    if not args.files:
        ap.error('no log files given')
    args.offset = parse_offset(args.utc_offset)
    if args.date:
        args.date = datetime.strptime(args.date, '%Y-%m-%d').date()
    else:
        args.date = date.today()
    files = load_files(args)
    add_cli_anchors(files, args)
    base = datetime.combine(args.date, time.min)
    align_old_files(files, args, base)
    estimate_old(files, args, base)
    events = []
    for f in files:
        events.extend(f.events)
    events.sort(key=lambda e: (display_dt(e, args), e.role))
    picked = selected(events, args)
    out = []
    if args.summary:
        out.extend(build_summary(files))
    if args.diff:
        out.extend(build_diff(picked, args))
    if not args.summary and not args.diff:
        out.extend(fmt_event(e, args) for e in picked)
    return [mask_ips(x) for x in out]


def selftest():
    with tempfile.TemporaryDirectory() as d:
        p1 = os.path.join(d, 'p1-trace.log')
        p2 = os.path.join(d, 'p2-trace.log')
        with open(p1, 'w', encoding='utf-8') as fh:
            fh.write('# OriCoop trace format=1 started_utc=2026-11-15T14:41:00.000Z utc_offset_min=60\n')
            fh.write('15:41:02.123 2026-11-15T14:41:02.123Z f=1 ff=1 t=1.0 sid=aa role=P1 enemy death id=Frog#123 x=1.5 msg="has spaces \\"q\\""\n')
            fh.write('15:41:04.000 2026-11-15T14:41:04.000Z f=2 ff=2 t=3.0 sid=aa role=P1 enemy death id=Bat#9\n')
        with open(p2, 'w', encoding='utf-8') as fh:
            fh.write('# OriCoop trace format=1 started_utc=2026-11-15T14:41:00.000Z utc_offset_min=60\n')
            fh.write('15:41:03.223 2026-11-15T14:41:03.223Z f=1 ff=1 t=1.0 sid=aa role=P2 enemy death id=Frog#123\n')
            fh.write('15:41:05.000 2026-11-15T14:41:05.000Z f=2 ff=2 t=3.0 sid=aa role=P2 enemy death id=Owl#7\n')
        merged = '\n'.join(run([p1, p2]))
        assert 'P1 enemy death id=Frog#123' in merged
        assert 'msg="has spaces \\"q\\""' in merged
        diff = '\n'.join(run([p1, p2, '--diff', '--window', '2.0']))
        assert 'ONLY P1:' in diff and 'ONLY P2:' in diff
        assert 'Frog#123' not in diff

        o1 = os.path.join(d, 'p1-old.log')
        o2 = os.path.join(d, 'p2-old.log')
        with open(o1, 'w', encoding='utf-8') as fh:
            fh.write('[Message:   BepInEx] BepInEx 5.4.23.6 - OriDE (1/2/2026 3:04:05 PM)\n')
            fh.write('[Info   :Ori Local Co-op] net: connected to 198.51.100.7:1234\n')
            fh.write('[Info   :Ori Local Co-op] net: partner runs 1.0\n')
            fh.write('[Info   :Ori Local Co-op] net: E3 remove enemy=1 2 3 4:5 class=StarSlugEnemy\n')
            fh.write('[Info   :Ori Local Co-op] p1-only marker\n')
        with open(o2, 'w', encoding='utf-8') as fh:
            for _ in range(5):
                fh.write('[Info   :Ori Local Co-op] boot line\n')
            fh.write('[Info   :Ori Local Co-op] net: connected to 198.51.100.8:4321\n')
            fh.write('[Info   :Ori Local Co-op] net: partner runs 1.0\n')
            fh.write('[Info   :Ori Local Co-op] net: E3 remove enemy=1 2 3 4:5 class=StarSlugEnemy\n')
        old_out = run([o1, o2, '--date', '2026-11-16'])
        old_text = '\n'.join(old_out)
        assert '~' in old_text and '198.51.100.7' not in old_text and 'ip#' in old_text
        conn = [x for x in old_out if ' net connected ' in x]
        assert len(conn) == 2 and conn[0].split()[0] == conn[1].split()[0]
        old_diff = '\n'.join(run([o1, o2, '--date', '2026-11-16', '--diff', '--window', '2']))
        assert 'ONLY P1:' in old_diff and 'p1-only' in old_diff


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    ARGS = None
    for line in run(sys.argv[1:]):
        print(line)
