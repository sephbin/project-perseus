if 'ALLOWED_HOSTS' not in locals():
    ALLOWED_HOSTS = ['localhost', '127.0.0.1']

# Force debug on in development
DEBUG = True


# optionally override any of these settings with an env.py file
try:
    from .local import *
except ModuleNotFoundError as e:
    if e.name != 'projectperseus.settings.local':
        raise
