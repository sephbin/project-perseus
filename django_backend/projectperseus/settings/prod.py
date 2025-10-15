import os

SECRET_KEY = os.environ.get("SECRET_KEY")
DEBUG = False

DATABASES = {
    'default': {
        'ENGINE': 'django.db.backends.mysql',
        'NAME': 'sephbin$perseus',
        'USER': 'sephbin',
        'PASSWORD': '100%StrongPassword',
        'HOST': 'sephbin.mysql.pythonanywhere-services.com',
        'OPTIONS':{
	        'init_command': "SET sql_mode='STRICT_TRANS_TABLES'",
        }
    }
}