from django.db import models


class Element(models.Model):
    unique_id = models.TextField(unique=True)
    name = models.TextField(blank=True)


class Parameter(models.Model):
    name = models.TextField(unique=True)
    value = models.TextField(blank=True, null=True)
    value_type = models.TextField(blank=True, null=True)
    element = models.ForeignKey('Element', on_delete=models.CASCADE, related_name='parameters')

    class Meta:
        unique_together = ('element', 'name')