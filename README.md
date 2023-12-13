# Project Perseus

## Dev setup

- Install python 3.11  
- Install postgres 12.17
- Setup a postgres user and database - both named `projectperseus` with password the same.
- Set `DJANGO_ENVIROMENT` environment variable to `DEVELOPMENT`
- Run `python manage.py collectstatic`
- Run `python manage.py migrate`
- Run `python manage.py createsuperuser`