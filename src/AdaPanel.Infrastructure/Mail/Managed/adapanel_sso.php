<?php
// Managed by AdaPanel. No panel administrator or primary mailbox credentials are stored here.
class adapanel_sso extends rcube_plugin
{
    private $grant = null;

    public function init()
    {
        $this->add_hook('startup', [$this, 'startup']);
        $this->add_hook('authenticate', [$this, 'authenticate']);
        $this->add_hook('login_after', [$this, 'login_after']);
        $this->add_hook('login_failed', [$this, 'login_failed']);
        $this->add_hook('session_destroy', [$this, 'session_destroy']);
    }

    public function startup($args)
    {
        $rc = rcmail::get_instance();
        if (!isset($_POST['adapanel_sso']) && isset($_SESSION['adapanel_sso_expiry']) && time() >= $_SESSION['adapanel_sso_expiry']) {
            $rc->kill_session();
            $rc->session->regenerate_id(true);
            return ['task' => 'login', 'action' => ''];
        }
        if (!isset($_POST['adapanel_sso'])) {
            return $args;
        }

        $ticket = $_POST['adapanel_sso'];
        unset($_POST['adapanel_sso'], $_REQUEST['adapanel_sso']);
        $host = strtolower($_SERVER['HTTP_HOST'] ?? '');
        $origin = $_SERVER['HTTP_ORIGIN'] ?? '';
        if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST'
            || !in_array(strtolower($_SERVER['HTTPS'] ?? ''), ['on', '1'], true)
            || !is_string($ticket) || !preg_match('/\A[A-Za-z0-9_-]{64}\z/D', $ticket)
            || !preg_match('/\Awebmail\.[a-z0-9.-]+\z/D', $host)
            || !preg_match('~\Ahttps?://[^/\r\n]+\z~D', $origin)) {
            $this->deny();
        }
        $this->grant = $this->broker('consume', ['ticket' => $ticket, 'host' => $host, 'origin' => $origin]);
        unset($ticket);
        if (!is_array($this->grant) || !is_string($this->grant['address'] ?? null)
            || !is_string($this->grant['password'] ?? null) || empty($this->grant['password'])
            || !is_string($this->grant['lease'] ?? null) || ($this->grant['expiresAt'] ?? 0) <= time()
            || substr(strtolower($this->grant['address']), strrpos($this->grant['address'], '@') + 1) !== substr($host, 8)) {
            $this->deny();
        }
        // Only an authenticated handoff may replace a session. No logout purge/expunge.
        $rc->kill_session();
        $rc->session->regenerate_id(true);
        return ['task' => 'login', 'action' => 'login'];
    }

    public function authenticate($args)
    {
        if ($this->grant !== null) {
            $args['user'] = $this->grant['address'];
            $args['pass'] = $this->grant['password'];
            $args['host'] = rcmail::get_instance()->config->get('imap_host');
            $args['cookiecheck'] = false;
            $args['valid'] = true;
            $args['abort'] = false;
            unset($this->grant['password']);
        }
        return $args;
    }

    public function login_after($args)
    {
        if ($this->grant !== null) {
            $_SESSION['adapanel_sso_lease'] = $this->grant['lease'];
            $_SESSION['adapanel_sso_expiry'] = $this->grant['expiresAt'];
            $this->grant = null;
        }
        return $args;
    }

    public function login_failed($args)
    {
        if ($this->grant !== null) {
            $this->broker('revoke', ['lease' => $this->grant['lease']]);
            $this->grant = null;
            $this->deny();
        }
        return $args;
    }

    public function session_destroy($args)
    {
        if (isset($_SESSION['adapanel_sso_lease'])) {
            $this->broker('revoke', ['lease' => $_SESSION['adapanel_sso_lease']]);
            unset($_SESSION['adapanel_sso_lease'], $_SESSION['adapanel_sso_expiry']);
        }
        return $args;
    }

    private function broker($action, $fields)
    {
        $rc = rcmail::get_instance();
        $settings = $rc->config->get('adapanel_sso');
        if (!is_array($settings) || !in_array($action, ['consume', 'revoke'], true)) {
            return null;
        }
        $key = @file_get_contents($settings['key_file']);
        if (!is_string($key) || !preg_match('/\A[A-Za-z0-9_-]{64}\z/D', $key)) {
            return null;
        }
        $curl = curl_init($settings['broker_url'] . '/' . $action);
        curl_setopt_array($curl, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => http_build_query($fields, '', '&'),
            CURLOPT_HTTPHEADER => ['X-AdaPanel-Webmail-Broker: ' . $key, 'Content-Type: application/x-www-form-urlencoded'],
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_CONNECTTIMEOUT => 5,
            CURLOPT_TIMEOUT => 25,
            CURLOPT_PROXY => '',
            CURLOPT_SSL_VERIFYPEER => true,
            CURLOPT_SSL_VERIFYHOST => 2,
            CURLOPT_CAINFO => $settings['ca_file'],
            CURLOPT_PROTOCOLS => CURLPROTO_HTTPS | CURLPROTO_HTTP,
        ]);
        $body = curl_exec($curl);
        $status = curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        curl_close($curl);
        unset($key);
        return $status === 200 && is_string($body) && strlen($body) <= 8192 ? json_decode($body, true) : null;
    }

    private function deny()
    {
        http_response_code(403);
        header('Cache-Control: no-store');
        header('Referrer-Policy: no-referrer');
        exit('Webmail sign-in could not be completed. Return to AdaPanel and try again.');
    }
}
