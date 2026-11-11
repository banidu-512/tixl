<?php
/**
 * TiXL collab backend - the party-matching lobby, whispers and WebRTC signaling for the
 * editor's session browser. Self-hostable: drop this file (plus .htaccess) into any PHP 8
 * web space; storage is SQLite when available, JSON files otherwise.
 *
 * Editors talk to it with plain HTTPS + JSON:
 *   POST ?op=register      host puts a session in the list (room, key, title, ...)
 *   POST ?op=heartbeat     host keeps it alive (key required)
 *   POST ?op=update        host edits the entry / unlists it (key required)
 *   POST ?op=unregister    host deletes the entry (key required)
 *   GET  ?op=list          the session browser: every listed, non-expired session
 *   POST ?op=whisper       a visitor messages the host of a session
 *   GET  ?op=whispers      host: every whisper for its room; visitor: for one name
 *   POST ?op=signal        relay-free WebRTC signaling: put an offer/answer
 *   GET  ?op=signal        relay-free WebRTC signaling: take one
 *   POST ?op=verify        Discord token check: {token} -> {name, lang, id, verified}
 *
 * The client is the TiXL editor (a desktop app), not a browser: no cookies, no CSRF,
 * JSON in and out. Secrets (room keys) are stored hashed. Everything user-visible is
 * length-capped and control-character free.
 */

declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');
header('X-Content-Type-Options: nosniff');

const ROOM_TTL_SECONDS = 90;          // a session dies from the list without heartbeats
const SIGNAL_TTL_SECONDS = 300;       // an unanswered offer goes away
const MAX_ROOMS = 200;
const MAX_WHISPERS_PER_ROOM = 40;
const MAX_NAME = 48;
const MAX_TEXT = 256;
const MAX_TICKETS_PER_ROOM = 16;
const MAX_MSGS_PER_TICKET = 32;
const MAX_TITLE = 64;
const MAX_PURPOSE = 24;
const MAX_PROJECT = 64;
const MAX_LANG = 12;
const MAX_ROOM_CODE = 12;
const MAX_KEY = 64;

// --- request ---------------------------------------------------------------------------------

$op = $_GET['op'] ?? '';
$input = array_merge($_GET, read_json_body());

try {
    match ($op) {
        'list' => respond_ok(['rooms' => list_rooms()]),
        'register' => op_register($input),
        'heartbeat' => op_heartbeat($input),
        'update' => op_update($input),
        'unregister' => op_unregister($input),
        'whisper' => op_whisper($input),
        'whispers' => op_whispers($input),
        'signal-put' => op_signal($input),
        'signal-get' => op_signal_get($input),
        'join-request' => op_join_request($input),
        'tickets' => op_tickets($input),
        'ticket-state' => op_ticket_state($input),
        'ticket-put' => op_signal_put($input),
        'ticket-get' => op_ticket_get($input),
        'verify' => op_verify($input),
        default => respond_error('unknown op'),
    };
} catch (Throwable $e) {
    http_response_code(500);
    // Temporary diagnosis aid: ?debug=1 reveals the underlying reason; remove once stable.
    $detail = isset($_GET['debug']) ? $e->getMessage() . ' @ ' . basename($e->getFile()) . ':' . $e->getLine() : '';
    echo json_encode(['ok' => false, 'error' => 'backend error', 'detail' => $detail]);
    error_log('tixl-api: ' . $e->getMessage());
}

// --- storage ---------------------------------------------------------------------------------

function db(): PDO
{
    static $pdo = null;
    if ($pdo !== null) {
        return $pdo;
    }

    // The database lives OUTSIDE the web root (a sibling of www/), because dotfile-based
    // protection is not honoured on this host: anything under the web root is downloadable.
    // Falls back to ./data when the parent is not writable (local development).
    $dir = dirname(dirname(__DIR__)) . '/tixl-data';
    if (!is_dir($dir) && !@mkdir($dir, 0775, true) && !is_dir($dir)) {
        $dir = __DIR__ . '/data';
    }

    if (!is_dir($dir) && !@mkdir($dir, 0775, true) && !is_dir($dir)) {
        throw new RuntimeException('cannot create the data folder (permissions?)');
    }

    $pdo = new PDO('sqlite:' . $dir . '/collab.sqlite', null, null, [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
    ]);
    $pdo->exec('PRAGMA journal_mode = WAL');
    $pdo->exec('PRAGMA busy_timeout = 3000');
    $pdo->exec('CREATE TABLE IF NOT EXISTS rooms (
        room TEXT PRIMARY KEY,
        key_hash TEXT NOT NULL,
        relay TEXT NOT NULL DEFAULT "",
        title TEXT NOT NULL DEFAULT "",
        purpose TEXT NOT NULL DEFAULT "",
        project TEXT NOT NULL DEFAULT "",
        host TEXT NOT NULL DEFAULT "",
        lang TEXT NOT NULL DEFAULT "",
        pwd INTEGER NOT NULL DEFAULT 0,
        listed INTEGER NOT NULL DEFAULT 0,
        members INTEGER NOT NULL DEFAULT 1,
        max_members INTEGER NOT NULL DEFAULT 18,
        whispers TEXT NOT NULL DEFAULT "[]",
        signals TEXT NOT NULL DEFAULT "[]",
        updated INTEGER NOT NULL DEFAULT 0
    )');

    // A database created by an older version may lack newer columns; add them in place.
    $columns = $pdo->query('PRAGMA table_info(rooms)')->fetchAll(PDO::FETCH_COLUMN, 1);
    if (!in_array('relay', $columns, true)) {
        $pdo->exec('ALTER TABLE rooms ADD COLUMN relay TEXT NOT NULL DEFAULT ""');
    }
    if (!in_array('tickets', $columns, true)) {
        $pdo->exec('ALTER TABLE rooms ADD COLUMN tickets TEXT NOT NULL DEFAULT "[]"');
    }

    return $pdo;
}

// --- helpers ---------------------------------------------------------------------------------

/** Trims, cuts to length and drops control characters; these strings end up in other people's UI. */
function clean(string $value, int $max): string
{
    $cleaned = trim(preg_replace('/[\x00-\x1F\x7F]/u', '', $value) ?? '');
    return mb_substr($cleaned, 0, $max);
}

function text_field(array $input, string $name, int $max): string
{
    return clean((string)($input[$name] ?? ''), $max);
}

function room_code(array $input): string
{
    $code = strtoupper(preg_replace('/[^A-Za-z0-9]/', '', (string)($input['room'] ?? '')) ?? '');
    return substr($code, 0, MAX_ROOM_CODE);
}

/** SDP needs its line breaks; strips everything else that is not printable or a newline. */
function sdp_clean(string $value): string
{
    $value = str_replace("\r\n", "\n", $value);
    $value = preg_replace('/[\x00-\x09\x0B-\x1F\x7F]/', '', $value) ?? '';
    return trim($value);
}

function key_hash(string $key): string
{
    return hash('sha256', $key);
}

function key_matches(array $room, string $key): bool
{
    return $room['key_hash'] === key_hash($key) && $key !== '';
}

function respond_ok(array $data = []): never
{
    echo json_encode(['ok' => true, ...$data], JSON_UNESCAPED_UNICODE);
    exit;
}

function respond_error(string $message, int $status = 200): never
{
    http_response_code($status);
    echo json_encode(['ok' => false, 'error' => $message], JSON_UNESCAPED_UNICODE);
    exit;
}

function read_json_body(): array
{
    $raw = file_get_contents('php://input') ?: '';
    if ($raw === '') {
        return [];
    }
    try {
        $decoded = json_decode($raw, true, 32);
        return is_array($decoded) ? $decoded : [];
    } catch (Throwable) {
        return [];
    }
}

function load_room(string $code): ?array
{
    if ($code === '') {
        return null;
    }
    $stmt = db()->prepare('SELECT * FROM rooms WHERE room = ?');
    $stmt->execute([$code]);
    $room = $stmt->fetch(PDO::FETCH_ASSOC);
    return $room === false ? null : $room;
}

function delete_stale_rooms(): void
{
    db()->prepare('DELETE FROM rooms WHERE updated < ?')
        ->execute([time() - ROOM_TTL_SECONDS]);
}

function decode_list(string $json, int $maxEntries): array
{
    $decoded = json_decode($json, true);
    return is_array($decoded) ? array_slice($decoded, -$maxEntries) : [];
}

// --- ops -------------------------------------------------------------------------------------

function op_register(array $input): void
{
    $room = room_code($input);
    $key = (string)($input['key'] ?? '');
    if ($room === '' || $key === '') {
        respond_error('room and key required');
    }

    delete_stale_rooms();
    if ((int)db()->query('SELECT COUNT(*) FROM rooms')->fetchColumn() >= MAX_ROOMS) {
        respond_error('the session list is full');
    }

    // A re-register of a live room with the same key is the host coming back (reconnect);
    // without the key it is a code collision and the caller gets a fresh code elsewhere.
    $existing = load_room($room);
    if ($existing !== null && !key_matches($existing, $key)) {
        respond_error('room code taken');
    }

    $relay = clean((string)($input['relay'] ?? ''), 64);
        if ($relay !== '' && !preg_match('/^[a-zA-Z0-9.\-]+:[0-9]{1,5}$/', $relay)) {
            respond_error('relay must be host:port');
        }

        // Fields the caller leaves out must not wipe what the room already has (a reconnecting
        // host sends a partial hello): the update merges, the insert fills defaults.
        $pwd = isset($input['pwd']) ? (!empty($input['pwd']) ? 1 : 0) : null;
        $listed = isset($input['listed']) ? (!empty($input['listed']) ? 1 : 0) : null;
        $members = isset($input['members']) ? max(1, (int)$input['members']) : null;
        $maxMembers = isset($input['max']) ? max(2, (int)$input['max']) : null;

        if ($existing !== null) {
            $stmt = db()->prepare('UPDATE rooms SET key_hash = ?, relay = CASE WHEN ? = "" THEN relay ELSE ? END,
                             title = CASE WHEN ? = "" THEN title ELSE ? END,
                             purpose = CASE WHEN ? = "" THEN purpose ELSE ? END,
                             project = CASE WHEN ? = "" THEN project ELSE ? END,
                             host = CASE WHEN ? = "" THEN host ELSE ? END,
                             lang = CASE WHEN ? = "" THEN lang ELSE ? END,
                             pwd = COALESCE(?, pwd),
                             listed = COALESCE(?, listed),
                             members = COALESCE(?, members),
                             max_members = COALESCE(?, max_members),
                             updated = ?
                           WHERE room = ?');
            $stmt->execute([
                key_hash($key),
                $relay, $relay,
                text_field($input, 'title', MAX_TITLE), text_field($input, 'title', MAX_TITLE),
                text_field($input, 'purpose', MAX_PURPOSE), text_field($input, 'purpose', MAX_PURPOSE),
                text_field($input, 'project', MAX_PROJECT), text_field($input, 'project', MAX_PROJECT),
                text_field($input, 'host', MAX_NAME), text_field($input, 'host', MAX_NAME),
                text_field($input, 'lang', MAX_LANG), text_field($input, 'lang', MAX_LANG),
                $pwd, $listed, $members, $maxMembers,
                time(),
                $room,
            ]);
            respond_ok(['room' => $room]);
        }

        $stmt = db()->prepare('INSERT INTO rooms (room, key_hash, relay, title, purpose, project, host, lang, pwd, listed, members, max_members, updated)
                           VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)');
    $stmt->execute([
        $room,
        key_hash($key),
        $relay,
        text_field($input, 'title', MAX_TITLE),
        text_field($input, 'purpose', MAX_PURPOSE),
        text_field($input, 'project', MAX_PROJECT),
        text_field($input, 'host', MAX_NAME),
        text_field($input, 'lang', MAX_LANG),
        $pwd ?? 0,
        $listed ?? 0,
        $members ?? 1,
        $maxMembers ?? 18,
        time(),
    ]);

    respond_ok(['room' => $room]);
}

function op_heartbeat(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }
    if (!key_matches($found, (string)($input['key'] ?? ''))) {
        respond_error('not the host');
    }

    db()->prepare('UPDATE rooms SET updated = ?, members = ? WHERE room = ?')
        ->execute([time(), max(1, (int)($input['members'] ?? 1)), $room]);
    respond_ok();
}

function op_update(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }
    if (!key_matches($found, (string)($input['key'] ?? ''))) {
        respond_error('not the host');
    }

    $title = text_field($input, 'title', MAX_TITLE);
    $purpose = text_field($input, 'purpose', MAX_PURPOSE);
    $project = text_field($input, 'project', MAX_PROJECT);
    $stmt = db()->prepare('UPDATE rooms SET title = CASE WHEN ? = "" THEN title ELSE ? END,
                                              purpose = CASE WHEN ? = "" THEN purpose ELSE ? END,
                                              project = CASE WHEN ? = "" THEN project ELSE ? END,
                                              listed = ?,
                                              updated = ?
                           WHERE room = ?');
    $stmt->execute([$title, $title, $purpose, $purpose, $project, $project,
                    !empty($input['listed']) ? 1 : 0, time(), $room]);
    respond_ok();
}

function op_unregister(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_ok();
    }
    if (!key_matches($found, (string)($input['key'] ?? ''))) {
        respond_error('not the host');
    }

    db()->prepare('DELETE FROM rooms WHERE room = ?')->execute([$room]);
    respond_ok();
}

function list_rooms(): array
{
    delete_stale_rooms();
    $rows = db()->query('SELECT room, relay, title, purpose, project, host, lang, pwd, members, max_members, updated
                         FROM rooms WHERE listed = 1 ORDER BY updated DESC LIMIT 200')->fetchAll(PDO::FETCH_ASSOC);
    $rooms = [];
    foreach ($rows as $row) {
        $rooms[] = [
            'code' => $row['room'],
            'relay' => $row['relay'],
            'title' => $row['title'],
            'purpose' => $row['purpose'],
            'project' => $row['project'],
            'host' => $row['host'],
            'lang' => $row['lang'],
            'pwd' => (bool)$row['pwd'],
            'members' => (int)$row['members'],
            'max' => (int)$row['max_members'],
            'age' => time() - (int)$row['updated'],
        ];
    }
    return $rooms;
}

function op_whisper(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $key = (string)($input['key'] ?? '');
    $text = text_field($input, 'text', MAX_TEXT);
    if ($text === '') {
        respond_error('text required');
    }

    // With the room key this is the host replying; the reply is visible to the visitor it names.
    // Without it this is a visitor whispering, which carries the visitor's name.
    $isHostReply = false;
    $from = '';
    $to = '';
    if ($key !== '') {
        if (!key_matches($found, $key)) {
            respond_error('not the host');
        }
        $isHostReply = true;
        $to = text_field($input, 'to', MAX_NAME);
        if ($to === '') {
            respond_error('to required');
        }
    } else {
        $from = text_field($input, 'name', MAX_NAME);
        if ($from === '') {
            respond_error('name required');
        }
    }

    append_whisper($room, ['from' => $isHostReply ? $found['host'] : $from,
                           'text' => $text,
                           'host' => $isHostReply,
                           'to' => $to,
                           'at' => time()]);
    respond_ok();
}

function op_whispers(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $since = (int)($input['since'] ?? 0);
    $whispers = decode_list((string)$found['whispers'], MAX_WHISPERS_PER_ROOM);

    // The host reads everything with its key; a visitor reads its own thread by name.
    $key = (string)($input['key'] ?? '');
    $name = text_field($input, 'name', MAX_NAME);
    if ($key !== '') {
        if (!key_matches($found, $key)) {
            respond_error('not the host');
        }
        respond_ok(['whispers' => array_values(array_filter($whispers, fn($w) => $w['at'] > $since))]);
    }

    if ($name === '') {
        respond_error('key or name required');
    }

    respond_ok(['whispers' => array_values(array_filter(
        $whispers,
        fn($w) => $w['at'] > $since && ($w['from'] === $name || ($w['host'] === true && ($w['to'] ?? '') === $name))
    ))]);
}

function append_whisper(string $room, array $entry): void
{
    with_room_lock(function () use ($room, $entry) {
        $fresh = load_room($room);
        $whispers = decode_list((string)($fresh['whispers'] ?? '[]'), MAX_WHISPERS_PER_ROOM);
        $whispers[] = $entry;
        if (count($whispers) > MAX_WHISPERS_PER_ROOM) {
            $whispers = array_slice($whispers, -MAX_WHISPERS_PER_ROOM);
        }

        db()->prepare('UPDATE rooms SET whispers = ? WHERE room = ?')
            ->execute([json_encode($whispers, JSON_UNESCAPED_UNICODE), $room]);
    });
}

// --- relay-free WebRTC signaling -------------------------------------------------------------

/** Puts an offer or answer. The host signs its offers with the room key; a guest's answer carries a nonce. */
function op_signal(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $kind = (string)($input['kind'] ?? '');
    if ($kind !== 'offer' && $kind !== 'answer') {
        respond_error('kind must be offer or answer');
    }

    $sdp = clean((string)($input['sdp'] ?? ''), 16384);
    if ($sdp === '') {
        respond_error('sdp required');
    }

    if ($kind === 'offer') {
        if (!key_matches($found, (string)($input['key'] ?? ''))) {
            respond_error('not the host');
        }
        $nonce = bin2hex(random_bytes(8));
        $signals = decode_list((string)$found['signals'], 16);
        $signals[] = ['kind' => 'offer', 'sdp' => $sdp, 'nonce' => $nonce, 'at' => time()];
        save_signals($room, $signals);
        respond_ok(['nonce' => $nonce]);
    }

    // An answer goes to exactly one offer's nonce, so no other visitor can read it.
    $nonce = substr(preg_replace('/[^a-f0-9]/', '', (string)($input['nonce'] ?? '')) ?? '', 0, 16);
    if ($nonce === '') {
        respond_error('nonce required');
    }

    $signals = decode_list((string)$found['signals'], 16);
    foreach ($signals as $index => $signal) {
        if ($signal['kind'] === 'offer' && $signal['nonce'] === $nonce) {
            // Write through the index: a foreach copy would silently drop the answer.
            $signals[$index]['answer'] = $sdp;
            $signals[$index]['answered'] = time();
            save_signals($room, $signals);
            respond_ok();
        }
    }

    respond_error('offer not found');
}

function save_signals(string $room, array $signals): void
{
    db()->prepare('UPDATE rooms SET signals = ?, updated = ? WHERE room = ?')
        ->execute([json_encode($signals, JSON_UNESCAPED_UNICODE), time(), $room]);
}

function op_signal_get(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $nonce = substr(preg_replace('/[^a-f0-9]/', '', (string)($input['nonce'] ?? '')) ?? '', 0, 16);
    $signals = decode_list((string)$found['signals'], 16);
    foreach ($signals as $signal) {
        if ($signal['nonce'] === $nonce) {
            respond_ok(['signal' => [
                'kind' => $signal['kind'],
                'sdp' => $signal['sdp'],
                'answer' => $signal['answer'] ?? null,
            ]]);
        }
    }

    respond_error('signal not found');
}

// --- join tickets: the relay-free session rendezvous ------------------------------------------

// A ticket is a two-party signal mailbox: the guest creates it with join-request, the host
// fills it with offer/candidates, the guest answers into it, and both sides poll until the
// WebRTC data channel takes over. Messages are the editor's own signal JSON
// ({t:"offer"|"answer"|"candidate", ...}) passed through unmodified.

function op_join_request(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $name = text_field($input, 'name', MAX_NAME);
    if ($name === '') {
        respond_error('name required');
    }

    $ticketId = bin2hex(random_bytes(8));
    with_room_lock(function () use ($room, $name, $input, $ticketId) {
        $fresh = load_room($room);
        $tickets = decode_list((string)($fresh['tickets'] ?? '[]'), MAX_TICKETS_PER_ROOM);
        $tickets[] = ['id' => $ticketId,
                      'name' => $name,
                      'lang' => text_field($input, 'lang', MAX_LANG),
                      'state' => 'pending',
                      'at' => time(),
                      'msgs' => []];
        if (count($tickets) > MAX_TICKETS_PER_ROOM) {
            $tickets = array_slice($tickets, -MAX_TICKETS_PER_ROOM);
        }

        save_tickets($room, $tickets);
    });

    respond_ok(['ticket' => $ticketId]);
}

/** Host: sets a join ticket's state (approved lets the negotiation start; rejected ends it). */
function op_ticket_state(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }
    if (!key_matches($found, (string)($input['key'] ?? ''))) {
        respond_error('not the host');
    }

    $ticketId = substr(preg_replace('/[^a-f0-9]/', '', (string)($input['ticket'] ?? '')) ?? '', 0, 16);
    $state = (string)($input['state'] ?? '');
    if (!in_array($state, ['approved', 'rejected'], true)) {
        respond_error('state must be approved or rejected');
    }

    $tickets = decode_list((string)$found['tickets'], MAX_TICKETS_PER_ROOM);
    foreach ($tickets as $index => $ticket) {
        if ($ticket['id'] === $ticketId) {
            with_room_lock(function () use ($room, $ticketId, $state) {
                $fresh = load_room($room);
                $current = decode_list((string)($fresh['tickets'] ?? '[]'), MAX_TICKETS_PER_ROOM);
                foreach ($current as $i => $t) {
                    if ($t['id'] === $ticketId) {
                        $current[$i]['state'] = $state;
                        $current[$i]['answered'] = time();
                        save_tickets($room, $current);
                        return;
                    }
                }
            });

            respond_ok();
        }
    }

    respond_error('ticket not found');
}

function op_tickets(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }
    if (!key_matches($found, (string)($input['key'] ?? ''))) {
        respond_error('not the host');
    }

    $tickets = decode_list((string)$found['tickets'], MAX_TICKETS_PER_ROOM);
    $out = [];
    foreach ($tickets as $ticket) {
        $out[] = ['id' => $ticket['id'],
                  'name' => $ticket['name'],
                  'lang' => $ticket['lang'] ?? '',
                  'state' => $ticket['state'] ?? 'pending',
                  'at' => $ticket['at'],
                  'msgs' => count($ticket['msgs'] ?? [])];
    }

    respond_ok(['tickets' => $out]);
}

function op_signal_put(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $ticketId = substr(preg_replace('/[^a-f0-9]/', '', (string)($input['ticket'] ?? '')) ?? '', 0, 16);
    if ($ticketId === '') {
        respond_error('ticket required');
    }

    $message = $input['message'] ?? null;
    if (!is_array($message)) {
        respond_error('message required');
    }

    $type = (string)($message['t'] ?? '');
    if (!in_array($type, ['offer', 'answer', 'ice'], true)) {
        respond_error('t must be offer, answer or ice');
    }

    // Offer and answer carry SDP (line breaks preserved); candidates carry candidate/mid.
    foreach (['sdp' => 16384, 'candidate' => 512, 'mid' => 16] as $field => $max) {
        if (isset($message[$field])) {
            $message[$field] = $field === 'sdp' ? sdp_clean((string)$message[$field]) : clean((string)$message[$field], $max);
        }
    }

    // The host signs its messages with the room key; a guest signs with its ticket id.
    $isHost = key_matches($found, (string)($input['key'] ?? ''));

    $foundTicket = false;
    $sawCount = 0;
    with_room_lock(function () use ($room, $ticketId, $isHost, $message, $type, &$foundTicket, &$sawCount) {
        // Re-read inside the write lock: a parallel post may have appended since the load above.
        $fresh = load_room($room);
        $tickets = decode_list((string)($fresh['tickets'] ?? '[]'), MAX_TICKETS_PER_ROOM);
        foreach ($tickets as $index => $ticket) {
            if ($ticket['id'] !== $ticketId) {
                continue;
            }

            $msgs = $ticket['msgs'] ?? [];
            $sawCount = count($msgs);
            $msgs[] = ['from' => $isHost ? 'host' : 'guest',
                       't' => $type,
                       'sdp' => $message['sdp'] ?? '',
                       'candidate' => $message['candidate'] ?? '',
                       'mid' => $message['mid'] ?? '',
                       'at' => time()];
            if (count($msgs) > MAX_MSGS_PER_TICKET) {
                $msgs = array_slice($msgs, -MAX_MSGS_PER_TICKET);
            }

            $tickets[$index]['msgs'] = $msgs;
            save_tickets($room, $tickets);
            $foundTicket = true;
            return;
        }
    });

    if (!$foundTicket) {
        respond_error('ticket not found');
    }

    respond_ok(['from' => $isHost ? 'host' : 'guest', 'saw' => $sawCount]);
}

function op_ticket_get(array $input): void
{
    $room = room_code($input);
    $found = load_room($room);
    if ($found === null) {
        respond_error('room not found');
    }

    $ticketId = substr(preg_replace('/[^a-f0-9]/', '', (string)($input['ticket'] ?? '')) ?? '', 0, 16);
    $since = (int)($input['since'] ?? 0);
    $tickets = decode_list((string)$found['tickets'], MAX_TICKETS_PER_ROOM);
    foreach ($tickets as $ticket) {
        if ($ticket['id'] !== $ticketId) {
            continue;
        }

        $msgs = array_slice($ticket['msgs'] ?? [], $since);
        respond_ok(['msgs' => $msgs, 'state' => $ticket['state'] ?? 'pending']);
    }

    respond_error('ticket not found');
}

function save_tickets(string $room, array $tickets): void
{
    db()->prepare('UPDATE rooms SET tickets = ?, updated = ? WHERE room = ?')
        ->execute([json_encode($tickets, JSON_UNESCAPED_UNICODE), time(), $room]);
}

/**
 * Serialises a read-modify-write on one room's JSON columns. A host posts offer and ICE
 * candidates in parallel; without the write lock each request read the ticket before the
 * others appended and the last writer silently dropped the rest (the offer vanished).
 */
function with_room_lock(callable $mutate): void
{
    $pdo = db();
    $pdo->exec('BEGIN IMMEDIATE');
    try
    {
        $mutate();
        $pdo->exec('COMMIT');
    }
    catch (Throwable $e)
    {
        $pdo->exec('ROLLBACK');
        throw $e;
    }
}

// --- Discord identity (the account layer) ----------------------------------------------------

/** Proxies Discord's /users/@me so a peer never sees another peer's token; the backend keeps the trust. */
function op_verify(array $input): void
{
    $token = text_field($input, 'token', 512);
    if ($token === '') {
        respond_error('token required');
    }

    $context = stream_context_create(['http' => [
        'method' => 'GET',
        'header' => "Authorization: Bearer $token\r\nUser-Agent: TiXLCollab\r\n",
        'timeout' => 5,
        'ignore_errors' => true,
    ]]);
    $body = @file_get_contents('https://discord.com/api/v10/users/@me', false, $context);
    if ($body === false) {
        respond_ok(['verified' => false]);
    }

    $user = json_decode($body, true);
    if (!is_array($user) || !isset($user['id'])) {
        respond_ok(['verified' => false]);
    }

    respond_ok([
        'verified' => true,
        'id' => (string)$user['id'],
        'name' => clean($user['global_name'] ?? ($user['username'] ?? ''), MAX_NAME),
        'lang' => clean($user['locale'] ?? '', MAX_LANG),
        'avatar' => clean((string)($user['avatar'] ?? ''), 64),
    ]);
}
