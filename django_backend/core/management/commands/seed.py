from django.conf import settings
from django.contrib.auth.models import User
from django.core.management import BaseCommand, call_command

from rest_framework.authtoken.models import Token

from projectperseus.settings import EnvironmentType


class Command(BaseCommand):
    help = "Seed database for testing and development."

    def add_arguments(self, parser):
        parser.add_argument('--token', type=str, default=None, help="Specify the API token to use for the admin user.")
        parser.add_argument('--flush', action='store_true', help="Whether to flush the existing database data.")
        parser.add_argument('--migrate', action='store_true', help="Whether to migrate the database first.")

    def handle(self, *args, **options):
        if settings.ENVIRONMENT_TYPE != EnvironmentType.DEVELOPMENT:
            self.stdout.write('Seeding failed: This is not a development environment!')
            return

        if options['migrate'] is not None:
            self.stdout.write('Migrating database...')
            call_command('migrate', '--noinput')

        if options['flush'] is not None:
            self.stdout.write('Flushing data...')
            call_command('flush', '--noinput')

        self.stdout.write('Seeding data...')

        admin = User.objects.create_superuser(username='admin', password='x')

        api_token = Token.objects.create(user=admin, key=options['token'])

        self.stdout.write('Done.')
        self.stdout.write('Admin user is "admin". Password is "x".')
        self.stdout.write(f'API Token is "{api_token}".')
