# A fake Auth0 for the security and load harnesses (QA-6, #242). It runs inside the throwaway Docker network, over TLS, with no
# internet. It serves exactly what the API asks of Auth0:
#   GET /.well-known/openid-configuration   the discovery document (written by the harness)
#   GET /.well-known/jwks.json              the public half of the harness's throwaway signing key
#   GET /userinfo                           name and email of the token's own sub, from users.json (a new account's first bearer call)
#   POST /oauth/token                       the web sign-in's code exchange: the "code" is base64url({"sub", "nonce"}), made by the harness,
#                                           and the answer carries an ID token for that sub, signed with the run's throwaway key
#   GET /healthz                            for the harness to know it is up
# It keeps nothing. The harness mints the bearer access tokens itself. Standard library only.
import base64
import hashlib
import json
import os
import ssl
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs

ROOT = os.environ.get('FAKE_AUTH0_DIR', '/fake')
PORT = int(os.environ.get('FAKE_AUTH0_PORT', '8443'))
CLIENT_ID = os.environ.get('FAKE_AUTH0_CLIENT_ID', 'cm-fake-web-client')

# DigestInfo for SHA-256 (RFC 8017, section 9.2): the prefix of an RSASSA-PKCS1-v1_5 signature.
SHA256_PREFIX = bytes.fromhex('3031300d060960864801650304020105000420')


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b'=').decode()


def b64url_decode(text):
    return base64.urlsafe_b64decode(text + '=' * (-len(text) % 4))


def read_file(name):
    with open(os.path.join(ROOT, name), 'rb') as f:
        return f.read()


def rs256(data, n, d):
    """RSASSA-PKCS1-v1_5 with SHA-256 in plain integer maths: the standard library has no RSA, and this is a throwaway key."""
    k = (n.bit_length() + 7) // 8
    digest_info = SHA256_PREFIX + hashlib.sha256(data).digest()
    encoded = b'\x00\x01' + b'\xff' * (k - len(digest_info) - 3) + b'\x00' + digest_info
    return pow(int.from_bytes(encoded, 'big'), d, n).to_bytes(k, 'big')


def make_id_token(sub, nonce, user):
    """An ID token for the web sign-in, from the fake's issuer for the web client, signed with signing.json's key."""
    key = json.loads(read_file('signing.json'))
    issuer = json.loads(read_file('openid-configuration.json'))['issuer']
    kid = json.loads(read_file('jwks.json'))['keys'][0]['kid']
    now = int(time.time())
    header = {'alg': 'RS256', 'typ': 'JWT', 'kid': kid}
    claims = {'iss': issuer, 'sub': sub, 'aud': CLIENT_ID, 'iat': now, 'exp': now + 3600, 'auth_time': now,
              'nonce': nonce, 'name': user['name'], 'email': user['email'], 'email_verified': True}
    signing_input = b64url(json.dumps(header, separators=(',', ':')).encode()) + '.' + b64url(json.dumps(claims, separators=(',', ':')).encode())
    n = int.from_bytes(b64url_decode(key['n']), 'big')
    d = int.from_bytes(b64url_decode(key['d']), 'big')
    return signing_input + '.' + b64url(rs256(signing_input.encode('ascii'), n, d))


def subject_of(header):
    """The sub claim of 'Bearer <jwt>', or None. The signature is not checked: the harness only mints tokens for its own users."""
    if not header or not header.startswith('Bearer '):
        return None
    parts = header[len('Bearer '):].strip().split('.')
    if len(parts) != 3:
        return None
    try:
        return json.loads(b64url_decode(parts[1])).get('sub')
    except (ValueError, TypeError, AttributeError):
        return None


class Handler(BaseHTTPRequestHandler):

    def version_string(self):
        return ''  # no "Server: Python/x.y" for a scanner to report: ZAP follows the API's redirect to Auth0 and reads the fake's answers too

    def reply(self, status, body, content_type='application/json'):
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        path = self.path.split('?')[0]
        if path == '/.well-known/openid-configuration':
            self.reply(200, read_file('openid-configuration.json'))
        elif path == '/.well-known/jwks.json':
            self.reply(200, read_file('jwks.json'))
        elif path == '/healthz':
            self.reply(200, b'ok', 'text/plain')
        elif path == '/userinfo':
            subject = subject_of(self.headers.get('Authorization'))
            user = json.loads(read_file('users.json')).get(subject) if subject else None
            if user is None:
                self.reply(401, b'{"error":"unauthorized"}')
            else:
                body = {'sub': subject, 'name': user['name'], 'email': user['email'], 'email_verified': True}
                self.reply(200, json.dumps(body).encode())
        else:
            self.reply(404, b'{"error":"not_found"}')

    def do_POST(self):
        if self.path.split('?')[0] != '/oauth/token':
            self.reply(404, b'{"error":"not_found"}')
            return
        form = parse_qs(self.rfile.read(int(self.headers.get('Content-Length') or 0)).decode())
        try:
            grant = json.loads(b64url_decode(form['code'][0]))
            user = json.loads(read_file('users.json'))[grant['sub']]
            token = make_id_token(grant['sub'], grant.get('nonce'), user)
        except (KeyError, IndexError, ValueError, TypeError):
            self.reply(400, b'{"error":"invalid_grant"}')
            return
        body = {'access_token': 'cm-fake-access-token', 'id_token': token, 'token_type': 'Bearer', 'expires_in': 3600}
        self.reply(200, json.dumps(body).encode())

    def log_message(self, fmt, *args):
        pass  # nothing in a path or header is worth logging, and tokens must never be


def main():
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(os.path.join(ROOT, 'server.pem'), os.path.join(ROOT, 'server.key'))
    server = ThreadingHTTPServer(('0.0.0.0', PORT), Handler)
    server.socket = context.wrap_socket(server.socket, server_side=True)
    print(f'fake auth0 listening on {PORT}', flush=True)
    server.serve_forever()


if __name__ == '__main__':
    sys.exit(main())
