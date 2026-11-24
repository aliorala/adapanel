<?php
declare(strict_types=1);
ini_set('display_errors', '0');
require_once __DIR__ . '/collector.php';

function respond(int $code, array $body): never {
    http_response_code($code);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    header('X-Robots-Tag: noindex, nofollow');
    echo json_encode($body, JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR);
    exit;
}

function run_endpoint(string $mode): never {
    if ($mode === 'admin' || $mode === 'summary') respond(410, ['error' => 'statistics_are_private']);
    if (strtolower((string)($_SERVER['HTTPS'] ?? '')) !== 'on' && ($_SERVER['HTTPS'] ?? '') !== '1')
        respond(426, ['error' => 'https_required']);
    try {
        $private = dirname(__DIR__) . '/App_Data/installation-stats';
        $config = json_decode(file_get_contents($private . '/config.json'), true, 16, JSON_THROW_ON_ERROR);
        if (!is_string($config['adminHash'] ?? null) || strlen($config['adminHash']) !== 64
            || !is_string($config['rateKey'] ?? null) || strlen($config['rateKey']) < 32)
            throw new RuntimeException('configuration_invalid');
        $method = $_SERVER['REQUEST_METHOD'] ?? '';
        if ($mode === 'heartbeat') {
            if ($method !== 'POST') respond(405, ['error' => 'post_required']);
            if (strtolower(trim(explode(';', $_SERVER['CONTENT_TYPE'] ?? '')[0])) !== 'application/json')
                respond(415, ['error' => 'json_required']);
            $raw = file_get_contents('php://input', false, null, 0, 2049);
            if (strlen($raw) > 2048) respond(413, ['error' => 'body_too_large']);
            try {
                $input = json_decode($raw, true, 16, JSON_THROW_ON_ERROR);
                if (!is_array($input)) throw new InvalidArgumentException('invalid_body');
                validate_report($input);
            } catch (JsonException | InvalidArgumentException $e) { respond(400, ['error' => 'invalid_report']); }
            $ticket = with_store($private, function (array &$state) use ($input, $config): ?array {
                // Do not trust client-controlled forwarded-IP headers.
                $now = time();
                $accepted = accept_report($state, $input, $_SERVER['REMOTE_ADDR'] ?? '', $now, $config['rateKey']);
                return $accepted ? reserve_geolocation($state, hash('sha256', $input['installationToken']), $now) : null;
            });
            if ($ticket !== null) {
                // Network lookup is outside the registry lock and cannot fail an accepted heartbeat.
                $geo = lookup_geolocation($ticket['ip']);
                try {
                    with_store($private, function (array &$state) use ($ticket, $geo): void { apply_geolocation($state, $ticket, $geo); });
                } catch (Throwable $e) { /* The heartbeat is already committed; location is best effort. */ }
            }
            respond(200, ['schemaVersion' => $input['schemaVersion'], 'accepted' => true]);
        }
        if ($method !== 'GET') respond(405, ['error' => 'get_required']);
        $result = with_store($private, function (array &$state) use ($mode): array {
            $now = time(); prune_records($state, $now);
            $result = summary($state, $now);
            return $result;
        });
        respond(200, $result);
    } catch (OverflowException $e) {
        header('Retry-After: 86400'); respond(429, ['error' => 'limit_reached']);
    } catch (Throwable $e) {
        // Never leak records, paths, credentials or exception bodies in public responses.
        respond(503, ['error' => 'temporarily_unavailable']);
    }
}
