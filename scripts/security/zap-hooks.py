# ZAP hook for the API scan (Run-ZapApi.ps1 copies it next to the OpenAPI file and passes --hook).
# It adds a replacer rule that sets the Authorization header on every request, taking the throwaway stack's bearer token from the
# container's environment (CM_ZAP_TOKEN). The token is never written to a file, a command line or the log.
import os


def zap_started(zap, target):
    token = os.environ.get('CM_ZAP_TOKEN')
    if not token:
        return
    zap.replacer.add_rule(
        description='cm-sec bearer', enabled='true', matchtype='REQ_HEADER', matchstring='Authorization',
        matchregex='false', replacement='Bearer ' + token, initiators='1,2,3,4,5,6,7,8,9,10,11,12,13,14')
    print('Authorization header rule installed')
