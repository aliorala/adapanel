<?php
declare(strict_types=1);

function auth_store(string $directory, callable $operation): array {
    $lock = fopen($directory . '/admin-auth.lock', 'c+');
    if ($lock === false) throw new RuntimeException('lock_unavailable');
    $temporary = null;
    try {
        if (!flock($lock, LOCK_EX | LOCK_NB)) throw new RuntimeException('busy');
        $path = $directory . '/admin-auth.json';
        if (!is_file($path) || filesize($path) > 1048576) throw new RuntimeException('auth_unavailable');
        $state = json_decode(file_get_contents($path), true, 32, JSON_THROW_ON_ERROR);
        if (!is_string($state['passwordHash'] ?? null) || !is_string($state['version'] ?? null)
            || !is_array($state['attempts'] ?? null)) throw new RuntimeException('auth_invalid');
        $before = json_encode($state, JSON_THROW_ON_ERROR);
        $result = $operation($state);
        $after = json_encode($state, JSON_THROW_ON_ERROR);
        if ($before !== $after) {
            $temporary = tempnam($directory, 'auth-');
            if ($temporary === false || file_put_contents($temporary, $after) !== strlen($after)
                || !rename($temporary, $path)) throw new RuntimeException('auth_write_failed');
            $temporary = null;
        }
        return $result;
    } finally {
        if (is_string($temporary) && is_file($temporary)) unlink($temporary);
        flock($lock, LOCK_UN); fclose($lock);
    }
}

function start_admin_session(string $directory): void {
    ini_set('session.use_strict_mode', '1');
    ini_set('session.use_only_cookies', '1');
    ini_set('session.use_trans_sid', '0');
    ini_set('session.gc_maxlifetime', '28800');
    session_save_path($directory . '/sessions');
    session_name('__Secure-AdaPanelAdmin');
    session_set_cookie_params(['lifetime'=>0,'path'=>'/admin','secure'=>true,'httponly'=>true,'samesite'=>'Strict']);
    if (!session_start()) throw new RuntimeException('session_unavailable');
    if (!isset($_SESSION['csrf'])) $_SESSION['csrf'] = bin2hex(random_bytes(32));
}

function signed_in(array $state, int $now): bool {
    return isset($_SESSION['version'], $_SESSION['issued'], $_SESSION['seen'])
        && hash_equals($state['version'], $_SESSION['version'])
        && $now >= $_SESSION['issued'] && $now - $_SESSION['issued'] < 28800
        && $now - $_SESSION['seen'] < 1800;
}

function establish_session(array $state, int $now): void {
    if (!session_regenerate_id(true)) throw new RuntimeException('session_rotation_failed');
    $_SESSION = ['version'=>$state['version'],'issued'=>$now,'seen'=>$now,'csrf'=>bin2hex(random_bytes(32))];
}

function session_view(bool $authenticated): array {
    return ['authenticated'=>$authenticated,'csrf'=>$_SESSION['csrf'],'username'=>$authenticated ? 'admin' : null];
}

// Shared between password changes and login; creating fresh browser sessions cannot reset limits.
function allow_password_attempt(array &$state, int $now): bool {
    foreach ($state['attempts'] as $key => $item) {
        if (!is_array($item) || ($item['until'] ?? 0) <= $now) unset($state['attempts'][$key]);
    }
    $ip = hash('sha256', (string)($_SERVER['REMOTE_ADDR'] ?? 'unknown'));
    if (($state['attempts'][$ip]['count'] ?? 0) >= 8 || ($state['attempts']['global']['count'] ?? 0) >= 100) return false;
    foreach ([$ip, 'global'] as $key) {
        if (!isset($state['attempts'][$key])) $state['attempts'][$key] = ['count'=>0,'until'=>$now+600];
        $state['attempts'][$key]['count']++;
    }
    return true;
}

function handle_auth(array &$state, string $action, array $input): array {
    $now = time();
    $authenticated = signed_in($state, $now);
    if ($authenticated) $_SESSION['seen'] = $now;
    if ($action === 'session') return [200, session_view($authenticated)];
    if ($action === 'logout') {
        $_SESSION = [];
        session_destroy();
        setcookie(session_name(), '', ['expires'=>1,'path'=>'/admin','secure'=>true,'httponly'=>true,'samesite'=>'Strict']);
        return [200, ['ok'=>true]];
    }
    if ($action === 'stats') return $authenticated ? [200, ['authorized'=>true]] : [401, ['error'=>'unauthorized']];
    if ($action === 'password' && !$authenticated) return [401, ['error'=>'unauthorized']];
    if (!in_array($action, ['login','password'], true)) return [404, ['error'=>'not_found']];
    $password = $input[$action === 'login' ? 'password' : 'currentPassword'] ?? null;
    if (!is_string($password) || strlen($password) > 72 || str_contains($password, "\0")) return [400, ['error'=>'invalid_input']];
    if (!allow_password_attempt($state, $now)) return [429, ['error'=>'too_many_attempts']];
    $valid = password_verify($password, $state['passwordHash']);
    if (!$valid || ($action === 'login' && ($input['username'] ?? '') !== 'admin'))
        return [401, ['error'=>$action === 'login' ? 'invalid_credentials' : 'wrong_current_password']];
    if ($action === 'password') {
        $next = $input['newPassword'] ?? null;
        if (!is_string($next) || strlen($next) > 72 || !preg_match('/\A.{6,}\z/usD', $next) || trim($next) === ''
            || str_contains($next, "\0") || $next !== ($input['confirmation'] ?? null)) return [400, ['error'=>'password_policy']];
        if (password_verify($next, $state['passwordHash'])) return [400, ['error'=>'same_password']];
        $state['passwordHash'] = password_hash($next, PASSWORD_BCRYPT, ['cost'=>12]);
        $state['version'] = bin2hex(random_bytes(32));
        $state['changedAtUtc'] = gmdate('Y-m-d\TH:i:s\Z');
    }
    unset($state['attempts'][hash('sha256', (string)($_SERVER['REMOTE_ADDR'] ?? 'unknown'))]);
    establish_session($state, $now);
    return [200, session_view(true)];
}
