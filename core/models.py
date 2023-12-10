from django.db import models


class Element(models.Model):
    id = models.BigIntegerField(primary_key=True)
    name = models.TextField(blank=True)
    comments = models.TextField(blank=True)