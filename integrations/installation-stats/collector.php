<?php
declare(strict_types=1);

const ACTIVE_SECONDS = 48 * 3600;
const RETENTION_SECONDS = 30 * 86400;

function utc_date(int $timestamp): string { return gmdate('Y-m-d\TH:i:s\Z', $timestamp); }

function migrate_record_metadata(array &$state): void {
    foreach ($state['records'] as &$record) {
        $allowed = ['id', 'version', 'firstSeen', 'lastSeen', 'shareDetails', 'ip', 'country', 'city', 'location', 'datacenter', 'lastDetailsAt', 'geoIp', 'geoUpdatedAt', 'geoAttemptAt'];
        $record = array_intersect_key($record, array_flip($allowed));
        $record['shareDetails'] = ($record['shareDetails'] ?? false) === true;
        foreach (['ip', 'country', 'city', 'location', 'datacenter'] as $field) {
            if (is_string($record[$field] ?? null) && $record[$field] !== '' && !isset($record['lastDetailsAt']))
                $record['lastDetailsAt'] = $record['lastSeen'];
        }
        if ((isset($record['country']) || isset($record['city'])) && isset($record['ip']) && !isset($record['geoUpdatedAt'])) {
            $record['geoIp'] = $record['ip']; $record['geoUpdatedAt'] = $record['lastDetailsAt'];
        }
    }
    unset($record);
}

function validate_report(array $input): array {
    $schema = $input['schemaVersion'] ?? null;
    if ($schema !== 1 && $schema !== 2) throw new InvalidArgumentException('invalid_schema');
    $keys = ['schemaVersion', 'installationToken', 'panelVersion'];
    if ($schema === 1) $keys = array_merge($keys, ['location', 'datacenter']);
    else $keys[] = 'shareDetails';
    if (array_diff(array_keys($input), $keys) || array_diff($keys, array_keys($input)))
        throw new InvalidArgumentException('invalid_schema');
    if (!is_string($input['installationToken']) || !preg_match('/\A[a-f0-9]{64}\z/D', $input['installationToken']))
        throw new InvalidArgumentException('invalid_token');
    if (!is_string($input['panelVersion']) || !preg_match('/\A[0-9]+\.[0-9]+\.[0-9]+(?:[.-][a-zA-Z0-9.-]+)?\z/D', $input['panelVersion']) || strlen($input['panelVersion']) > 64)
        throw new InvalidArgumentException('invalid_version');
    foreach ($schema === 1 ? ['location', 'datacenter'] : [] as $field) {
        if (!is_string($input[$field]) || strlen($input[$field]) > 400 || preg_match('/[\x00-\x1f\x7f]/', $input[$field]))
            throw new InvalidArgumentException('invalid_metadata');
    }
    if ($schema === 2 && !is_bool($input['shareDetails'])) throw new InvalidArgumentException('invalid_details');
    // Legacy clients remain compatible, but their optional metadata is discarded.
    return array_intersect_key($input, array_flip(['schemaVersion', 'installationToken', 'panelVersion']))
        + ['shareDetails' => $schema === 1 || $input['shareDetails']];
}

function lookup_geolocation(string $ip, ?callable $fetch = null): array {
    if (!filter_var($ip, FILTER_VALIDATE_IP, FILTER_FLAG_NO_PRIV_RANGE | FILTER_FLAG_NO_RES_RANGE)) return [];
    try {
        $url = 'https://ipwho.is/' . rawurlencode($ip) . '?fields=success,country,city,country_code';
        if ($fetch === null) $fetch = static function (string $url): string|false {
            if (!function_exists('curl_init')) return false;
            $curl = curl_init($url);
            if ($curl === false) return false;
            $body = '';
            try {
                curl_setopt_array($curl, [
                    CURLOPT_CONNECTTIMEOUT_MS => 2000, CURLOPT_TIMEOUT_MS => 2000,
                    CURLOPT_FOLLOWLOCATION => false, CURLOPT_MAXREDIRS => 0,
                    CURLOPT_PROTOCOLS => CURLPROTO_HTTPS, CURLOPT_REDIR_PROTOCOLS => CURLPROTO_HTTPS,
                    CURLOPT_SSL_VERIFYPEER => true, CURLOPT_SSL_VERIFYHOST => 2,
                    CURLOPT_WRITEFUNCTION => static function ($handle, string $chunk) use (&$body): int {
                        if (strlen($body) + strlen($chunk) > 4096) return 0;
                        $body .= $chunk; return strlen($chunk);
                    }
                ]);
                return curl_exec($curl) === true && curl_getinfo($curl, CURLINFO_RESPONSE_CODE) === 200 ? $body : false;
            } finally { curl_close($curl); }
        };
        $raw = $fetch($url);
        if (!is_string($raw) || strlen($raw) > 4096) return [];
        $record = json_decode($raw, true, 8, JSON_THROW_ON_ERROR);
        if (!is_array($record) || ($record['success'] ?? false) !== true) return [];
        $result = [];
        foreach (['country', 'city'] as $field) {
            $value = $record[$field] ?? null;
            if (is_string($value) && $value !== '' && strlen($value) <= 400 && !preg_match('/[\x00-\x1f\x7f]/', $value)) $result[$field] = $value;
        }
        return $result;
    } catch (Throwable $e) { return []; }
}

function reserve_geolocation(array &$state, string $id, int $now): ?array {
    $record = $state['records'][$id] ?? null;
    if (!is_array($record) || ($record['shareDetails'] ?? false) !== true
        || !filter_var($record['ip'] ?? '', FILTER_VALIDATE_IP, FILTER_FLAG_NO_PRIV_RANGE | FILTER_FLAG_NO_RES_RANGE)
        || ($record['geoAttemptAt'] ?? 0) > $now - 86400) return null;
    $day = gmdate('Y-m-d', $now);
    if (($state['geoDaily'][$day] ?? 0) >= 900) return null;
    $state['geoDaily'][$day] = ($state['geoDaily'][$day] ?? 0) + 1;
    $state['records'][$id]['geoAttemptAt'] = $now;
    return ['id' => $id, 'ip' => $record['ip'], 'lastSeen' => $record['lastSeen'], 'geoAttemptAt' => $now];
}

function apply_geolocation(array &$state, array $ticket, array $geo): void {
    $record = $state['records'][$ticket['id']] ?? null;
    if (!is_array($record) || ($record['shareDetails'] ?? false) !== true
        || ($record['ip'] ?? null) !== $ticket['ip'] || $record['lastSeen'] !== $ticket['lastSeen']
        || ($record['geoAttemptAt'] ?? null) !== $ticket['geoAttemptAt']) return;
    $geo = array_intersect_key($geo, array_flip(['country', 'city']));
    if ($geo === []) return;
    // Keep successful historic location untouched on provider failure. Successful
    // results replace the location tuple with the IP and timestamp it belongs to.
    unset($state['records'][$ticket['id']]['country'], $state['records'][$ticket['id']]['city']);
    $state['records'][$ticket['id']] = array_replace($state['records'][$ticket['id']], $geo,
        ['geoIp' => $ticket['ip'], 'geoUpdatedAt' => $ticket['geoAttemptAt']]);
}

function prune_records(array &$state, int $now): void {
    migrate_record_metadata($state);
    foreach ($state['records'] as $key => $record) {
        if ($record['lastSeen'] <= $now - RETENTION_SECONDS) unset($state['records'][$key]);
    }
    foreach ($state['daily'] as $day => $limits) {
        if ($day < gmdate('Y-m-d', $now - 86400)) unset($state['daily'][$day]);
    }
    foreach ($state['geoDaily'] ?? [] as $day => $limits) {
        if ($day < gmdate('Y-m-d', $now - 86400)) unset($state['geoDaily'][$day]);
    }
}

function accept_report(array &$state, array $input, string $peerIp, int $now, string $rateKey): bool {
    $input = validate_report($input);
    if (!filter_var($peerIp, FILTER_VALIDATE_IP)) throw new InvalidArgumentException('invalid_peer');
    prune_records($state, $now);
    $id = hash('sha256', $input['installationToken']);
    $existing = $state['records'][$id] ?? null;
    if ($existing === null) {
        if (count($state['records']) >= 50000) throw new OverflowException('capacity');
        $day = gmdate('Y-m-d', $now);
        $rateId = hash_hmac('sha256', $day . '|' . $peerIp, $rateKey);
        if (($state['daily'][$day][$rateId] ?? 0) >= 20) throw new OverflowException('registration_limit');
        $state['daily'][$day][$rateId] = ($state['daily'][$day][$rateId] ?? 0) + 1;
    } elseif ($now - $existing['lastSeen'] < 3600) {
        // Preserve genuine historical details; a preference change is not erasure.
        if (!$input['shareDetails']) {
            $state['records'][$id]['shareDetails'] = false;
            $state['records'][$id]['version'] = $input['panelVersion'];
        }
        // Duplicate/replayed reports are acknowledged without extending activity or adding a record.
        return false;
    }
    $state['records'][$id] = array_replace($existing ?? [], [
        'id' => $id, 'version' => $input['panelVersion'],
        'firstSeen' => $existing['firstSeen'] ?? $now, 'lastSeen' => $now,
        'shareDetails' => $input['shareDetails']
    ]);
    if ($input['shareDetails']) {
        $state['records'][$id]['ip'] = $peerIp;
        $state['records'][$id]['lastDetailsAt'] = $now;
    }
    return true;
}

function summary(array $state, int $now): array {
    $active = 0;
    foreach ($state['records'] as $record) {
        if ($record['lastSeen'] > $now - ACTIVE_SECONDS && $record['lastSeen'] <= $now) $active++;
    }
    return ['schemaVersion' => 1, 'activeInstallations' => $active, 'windowHours' => 48, 'measuredAtUtc' => utc_date($now)];
}

function with_store(string $directory, callable $operation): mixed {
    if (!is_dir($directory) || !is_writable($directory)) throw new RuntimeException('store_unavailable');
    $lock = fopen($directory . '/registry.lock', 'c+');
    if ($lock === false) throw new RuntimeException('lock_unavailable');
    $tmp = null;
    try {
        if (!flock($lock, LOCK_EX | LOCK_NB)) throw new RuntimeException('store_busy');
        $path = $directory . '/registry.json';
        if (!is_file($path)) throw new RuntimeException('store_missing');
        if (filesize($path) > 32 * 1024 * 1024) throw new RuntimeException('store_too_large');
        $state = json_decode(file_get_contents($path), true, 64, JSON_THROW_ON_ERROR);
        if (($state['schemaVersion'] ?? null) !== 1 || !is_array($state['records'] ?? null) || !is_array($state['daily'] ?? null))
            throw new RuntimeException('store_invalid');
        foreach ($state['records'] as $id => $record) {
            if (!is_array($record) || !is_int($record['lastSeen'] ?? null) || !is_int($record['firstSeen'] ?? null)
                || !is_string($id) || !preg_match('/\A[a-f0-9]{64}\z/D', $id)) throw new RuntimeException('record_invalid');
        }
        $before = json_encode($state, JSON_THROW_ON_ERROR);
        // Migrate under the same lock and atomic write as ordinary updates.
        // Legacy genuine metadata becomes explicitly timestamped history.
        migrate_record_metadata($state);
        $result = $operation($state);
        $after = json_encode($state, JSON_THROW_ON_ERROR | JSON_UNESCAPED_UNICODE);
        if ($after !== $before) {
            $tmp = tempnam($directory, 'registry-');
            if ($tmp === false || file_put_contents($tmp, $after) !== strlen($after)) throw new RuntimeException('write_failed');
            if (!rename($tmp, $path)) throw new RuntimeException('publish_failed');
            $tmp = null;
        }
        return $result;
    } finally {
        if (is_string($tmp) && is_file($tmp)) unlink($tmp);
        flock($lock, LOCK_UN); fclose($lock);
    }
}
