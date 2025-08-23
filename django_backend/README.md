# Project Perseus - Django Backend

## Project Structure

`core` - Core app. Contains the core application logic of the project.  
`projectperseus` - Base Django project/settings/etc. No application logic.  
`static` - Static files. Folder should remain empty in version control by way of the included .gitignore file.  
`web_api` - Web API relvant code. Contains only the web API functionality. Views, serializers, etc - no application logic.

## Dev setup

- Install python 3.11  
- Install postgres 12.17
- Setup a postgres user and database - both named `projectperseus` with password the same.
- Set `DJANGO_ENVIROMENT` environment variable to `DEVELOPMENT`
- Run `python manage.py collectstatic`
- Run `python manage.py seed --flush --migrate` to seed the database with basic data.
- Run `python manage.py createsuperuser`

## Seed - Specify an API token

You can specify an API token in the seed command that be given to the admin user, instead of generating a new one. This is useful to avoid having the token change constantly between seeds.  
To do this, run `python manage.py seed --flush --migrate --token <token>`

## Local settings
Any local Django settings can be placed in `projectperseus/settings/local.py` and will be loaded automatically. You will need to create said file first.
