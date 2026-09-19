#!/usr/bin/env python3
"""Compare OAuth client-secret POST and Basic authentication without printing credentials/tokens."""
import argparse
import base64
import getpass
import json
import sys
import urllib.error
import urllib.parse
import urllib.request

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--server', required=True)
    parser.add_argument('--realm', required=True)
    parser.add_argument('--client', required=True)
    args = parser.parse_args()
    server = urllib.parse.urlsplit(args.server)
    if server.scheme != 'https' or not server.hostname or server.username or server.password or server.query or server.fragment:
        parser.error('Use an HTTPS server base URL without credentials, query, or fragment.')
    if not sys.stdin.isatty():
        parser.error('Run interactively so the secret can be entered without echo.')
    secret = getpass.getpass('Client secret (hidden): ')
    url = args.server.rstrip('/') + '/realms/' + urllib.parse.quote(args.realm, safe='') + '/protocol/openid-connect/token'
    opener = urllib.request.build_opener(NoRedirect())
    known_errors = {'invalid_client', 'unauthorized_client', 'invalid_grant', 'invalid_request',
                    'invalid_scope', 'unsupported_grant_type', 'access_denied'}
    for method in ('client_secret_post', 'client_secret_basic'):
        fields = {'grant_type': 'client_credentials'}
        headers = {'Content-Type': 'application/x-www-form-urlencoded'}
        if method == 'client_secret_post':
            fields.update(client_id=args.client, client_secret=secret)
        else:
            pair = urllib.parse.quote_plus(args.client) + ':' + urllib.parse.quote_plus(secret)
            headers['Authorization'] = 'Basic ' + base64.b64encode(pair.encode()).decode()
        request = urllib.request.Request(url, data=urllib.parse.urlencode(fields).encode(), headers=headers)
        try:
            response = opener.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        except (urllib.error.URLError, TimeoutError, OSError):
            print(method + ': transport/TLS failure (no credentials or token printed)')
            continue
        with response:
            try:
                body = json.loads(response.read(262144))
            except (ValueError, UnicodeError):
                body = {}
            if not isinstance(body, dict):
                body = {}
            if response.status == 200 and isinstance(body.get('access_token'), str) and body['access_token']:
                result = 'token issued successfully (not displayed)'
            elif body.get('error') in known_errors:
                result = body['error']
            else:
                result = 'unrecognized response (not displayed)'
            print(method + ': HTTP ' + str(response.status) + ' — ' + result)

if __name__ == '__main__':
    main()
