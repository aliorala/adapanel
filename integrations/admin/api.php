<?php
declare(strict_types=1);
ini_set('display_errors', '0');
require_once __DIR__ . '/auth.php';
require_once dirname(__DIR__) . '/installation-stats/collector.php';

function reply(int $code, array $data): never {
    http_response_code($code);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Robots-Tag: noindex, nofollow');
    header('X-Content-Type-Options: nosniff');
    if ($code === 429) header('Retry-After: 600');
    echo json_encode($data, JSON_THROW_ON_ERROR | JSON_UNESCAPED_UNICODE);
    exit;
}

try {
    if (!in_array(strtolower((string)($_SERVER['HTTPS'] ?? '')), ['on','1'], true)) reply(426, ['error'=>'https_required']);
    $action = $_GET['action'] ?? '';
    if (!is_string($action) || !in_array($action, ['session','login','logout','password','stats'], true)) reply(404, ['error'=>'not_found']);
    $method = in_array($action, ['session','stats'], true) ? 'GET' : 'POST';
    if (($_SERVER['REQUEST_METHOD'] ?? '') !== $method) reply(405, ['error'=>'method_not_allowed']);
    $private = dirname(__DIR__) . '/App_Data/installation-stats';
    $input = [];
    if ($method === 'POST') {
        // Fixed origin, never derived from attacker-controlled Host or forwarded headers.
        if (($_SERVER['HTTP_ORIGIN'] ?? '') !== 'https://adapanel.host') reply(403, ['error'=>'origin_rejected']);
        if (strtolower(trim(explode(';', $_SERVER['CONTENT_TYPE'] ?? '')[0])) !== 'application/json') reply(415, ['error'=>'json_required']);
        $raw = file_get_contents('php://input', false, null, 0, 4097);
        if ($raw === false || strlen($raw) > 4096) reply(413, ['error'=>'body_too_large']);
        try { $input = json_decode($raw, true, 8, JSON_THROW_ON_ERROR); }
        catch (JsonException $e) { reply(400, ['error'=>'invalid_input']); }
        if (!is_array($input)) reply(400, ['error'=>'invalid_input']);
    }
    start_admin_session($private);
    if ($method === 'POST' && !hash_equals($_SESSION['csrf'], (string)($_SERVER['HTTP_X_CSRF_TOKEN'] ?? ''))) reply(403, ['error'=>'csrf_rejected']);
    [$code, $data] = auth_store($private, fn(array &$state) => handle_auth($state, $action, $input));
    if ($action === 'stats' && $code === 200) {
        $data = with_store($private, function(array &$state): array {
            $now = time(); prune_records($state, $now);
            $result = summary($state, $now);
            $fields = array_flip(['id','version','firstSeen','lastSeen','shareDetails','ip','country','city','location','datacenter','lastDetailsAt','geoIp','geoUpdatedAt']);
            $result['installations'] = array_values(array_map(
                fn($record) => array_intersect_key($record, $fields), $state['records']));
            usort($result['installations'], fn($a,$b) => $b['lastSeen'] <=> $a['lastSeen']);
            return $result;
        });
    }
    session_write_close();
    reply($code, $data);
} catch (Throwable $e) { reply(503, ['error'=>'temporarily_unavailable']); }
