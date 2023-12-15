from django.db import models


class Element(models.Model):
    id = models.BigIntegerField(primary_key=True)
    unique_id = models.UUIDField(unique=True)
    name = models.TextField(blank=True)
    comments = models.TextField(blank=True)