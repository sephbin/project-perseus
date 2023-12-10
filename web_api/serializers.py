from django.contrib.auth.models import User, Group
from rest_framework import serializers

from core.models import Element


class ElementSerializer(serializers.ModelSerializer):
    class Meta:
        model = Element
        fields = '__all__'


class ElementListSerializer(serializers.ListSerializer):
    child = ElementSerializer()
