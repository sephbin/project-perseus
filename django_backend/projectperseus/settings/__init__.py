from .base import *

env = os.getenv('DJANGO_ENVIRONMENT')
if env is None:
    raise Exception('The environment variable DJANGO_ENVIRONMENT must be set to one of: '
                    'DEVELOPMENT, STAGING, PRODUCTION')

from enum import Enum

class EnvironmentType(Enum):
    DEVELOPMENT = 1
    PRODUCTION = 2
    STAGING = 3

ENVIRONMENT_TYPE = EnvironmentType[env]
if ENVIRONMENT_TYPE in [EnvironmentType.PRODUCTION, EnvironmentType.STAGING]:
    from .prod import *
elif ENVIRONMENT_TYPE == EnvironmentType.DEVELOPMENT:
    from .dev import *
else:
    raise Exception(f'Unrecognized DJANGO_ENVIRONMENT set: {ENVIRONMENT_TYPE}')

# Sanity check to ensure the secret key is secure if it needs to be
if ENVIRONMENT_TYPE in [EnvironmentType.PRODUCTION, EnvironmentType.STAGING]:
    if 'insecure' in SECRET_KEY or len(SECRET_KEY) < 32:
        raise Exception('You must set the SECRET_KEY to something secure before running in production or staging.')