<?php
// Run with php -n: all curl calls below are sandbox fixtures; no network connection occurs.
$names = ['CURLOPT_POST', 'CURLOPT_POSTFIELDS', 'CURLOPT_HTTPHEADER', 'CURLOPT_RETURNTRANSFER',
    'CURLOPT_FOLLOWLOCATION', 'CURLOPT_CONNECTTIMEOUT', 'CURLOPT_TIMEOUT', 'CURLOPT_PROXY',
    'CURLOPT_SSL_VERIFYPEER', 'CURLOPT_SSL_VERIFYHOST', 'CURLOPT_CAINFO', 'CURLOPT_PROTOCOLS',
    'CURLPROTO_HTTPS', 'CURLPROTO_HTTP', 'CURLINFO_RESPONSE_CODE'];
foreach ($names as $n => $name) define($name, $n + 1);
$calls = [];
$mode = $argv[2] ?? 'success';
function check($condition, $message) { if (!$condition) throw new Exception($message); }
function curl_init($url) { return (object)['url' => $url, 'options' => []]; }
function curl_setopt_array($curl, $options) { $curl->options = $options; }
function curl_exec($curl) {
    global $calls;
    check($curl->options[CURLOPT_FOLLOWLOCATION] === false, 'no broker redirects');
    check($curl->options[CURLOPT_SSL_VERIFYPEER] === true, 'TLS chain must be checked');
    check($curl->options[CURLOPT_SSL_VERIFYHOST] === 2, 'TLS name must be checked');
    check($curl->options[CURLOPT_PROXY] === '', 'backchannel must remain local');
    check(strpos($curl->url, str_repeat('T', 64)) === false, 'ticket must not appear in URL');
    $calls[] = $curl;
    if (str_ends_with($curl->url, '/revoke')) return '{"revoked":true}';
    return json_encode(['address' => 'selected@example.test', 'password' => 'app_fixture_only',
        'lease' => 'owned-fixture-lease', 'expiresAt' => time() + 120]);
}
function curl_getinfo($curl, $kind) { return 200; }
function curl_close($curl) {}
class rcube_plugin { public $hooks = []; public function add_hook($name, $callback) { $this->hooks[$name] = $callback; } }
class FakeConfig {
    public $values;
    public function get($key) { return $this->values[$key] ?? null; }
}
class FakeSession {
    public $rotations = 0;
    public function regenerate_id($destroy) { check($destroy === true, 'old identifier must be invalidated'); $this->rotations++; }
}
class rcmail {
    public static $instance;
    public $config;
    public $session;
    public $kills = 0;
    public $plugin;
    public static function get_instance() { return self::$instance; }
    public function kill_session() {
        $this->kills++;
        if ($this->plugin) $this->plugin->session_destroy([]);
        $_SESSION = ['temp' => true];
    }
    public function logout_actions() { throw new Exception('handoff must never purge or expunge'); }
}
$keyfile = tempnam(sys_get_temp_dir(), 'sso-fixture-');
register_shutdown_function(function () use ($keyfile) { unlink($keyfile); });
file_put_contents($keyfile, str_repeat('A', 64));
$rc = new rcmail();
rcmail::$instance = $rc;
$rc->config = new FakeConfig();
$rc->config->values = [
    'imap_host' => 'tls://localhost:143',
    'adapanel_sso' => ['key_file' => $keyfile, 'broker_url' => 'https://localhost:8888/api/internal/webmail-sso', 'ca_file' => 'fixture.pem']
];
$rc->session = new FakeSession();
require $argv[1];
$plugin = new adapanel_sso();
$rc->plugin = $plugin;
$plugin->init();
register_shutdown_function(function () use ($mode, $rc) {
    global $calls;
    if (in_array($mode, ['http', 'wrong-host', 'login-failure'], true)) {
        check(http_response_code() === 403, 'failed handoff must deny with HTTP 403');
        check($rc->kills === ($mode === 'login-failure' ? 1 : 0), 'unauthenticated input must not destroy another session');
        if ($mode !== 'login-failure') check(($_SESSION['user_id'] ?? null) === 77, 'invalid handoff must preserve the previous session without displaying it');
        if ($mode === 'login-failure') check(count($calls) === 2 && str_ends_with($calls[1]->url, '/revoke'), 'login failure must revoke its own credential');
        echo "\nPASS deny-" . $mode . "\n";
    }
});
check(isset($plugin->hooks['startup'], $plugin->hooks['authenticate'], $plugin->hooks['session_destroy']), 'required hooks');
$_SESSION = ['user_id' => 77, 'username' => 'old@example.test'];
$_SERVER = ['REQUEST_METHOD' => 'POST', 'HTTPS' => 'on', 'HTTP_HOST' => 'webmail.example.test', 'HTTP_ORIGIN' => 'https://panel.example.test'];
$_POST = ['adapanel_sso' => str_repeat('T', 64)];
$_REQUEST = $_POST;
if ($mode === 'expired-handoff') $_SESSION['adapanel_sso_expiry'] = time() - 1;
if ($mode === 'http') $_SERVER['HTTPS'] = 'off';
if ($mode === 'wrong-host') $_SERVER['HTTP_HOST'] = 'webmail.other.test';
if ($mode === 'query-ticket') { $_GET['adapanel_sso'] = $_POST['adapanel_sso']; $_POST = []; }
$result = $plugin->startup(['task' => 'mail', 'action' => '']);
if ($mode === 'query-ticket') {
    check($result['task'] === 'mail' && count($calls) === 0, 'GET token must never trigger sign-in');
    echo "PASS query-ticket\n";
    exit;
}
check($result['task'] === 'login' && $result['action'] === 'login', 'real Roundcube login action required');
check($rc->kills === 1 && $rc->session->rotations === 1, 'old session destroyed and identifier rotated');
check(!isset($_SESSION['user_id']), 'old mailbox must be gone');
$auth = $plugin->authenticate(['user' => 'old@example.test', 'pass' => 'old', 'valid' => false, 'abort' => true]);
check($auth['user'] === 'selected@example.test' && $auth['pass'] === 'app_fixture_only', 'exact selected mailbox and temporary secret');
check($auth['host'] === 'tls://localhost:143' && $auth['valid'] === true && $auth['abort'] === false, 'normal IMAP authentication must execute');
if ($mode === 'login-failure') $plugin->login_failed([]);
$plugin->login_after([]);
check($_SESSION['adapanel_sso_lease'] === 'owned-fixture-lease', 'owned lease must survive login');
check(!isset($_SESSION['password']) && !isset($_SESSION['adapanel_sso_password']), 'plugin must not persist plaintext secrets');
$_SESSION['user_id'] = 99;
$_SESSION['adapanel_sso_expiry'] = time() - 1;
$_POST = [];
$expired = $plugin->startup(['task' => 'mail', 'action' => '']);
check($expired['task'] === 'login' && !isset($_SESSION['user_id']), 'absolute lease expiry must terminate session before reconnection');
check($rc->kills === 2 && $rc->session->rotations === 2, 'expired session rotated');
check(count($calls) === 2 && str_ends_with($calls[1]->url, '/revoke'), 'expiry must revoke only the owned lease');
echo "PASS success\n";
