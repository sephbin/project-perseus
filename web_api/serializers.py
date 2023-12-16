from rest_framework import serializers

from core.models import Element


class ElementSerializer(serializers.ModelSerializer):
    class Meta:
        model = Element
        fields = '__all__'
        extra_kwargs = {
            'unique_id': {'source': 'UniqueId'}
        }


class ElementListSerializer(serializers.ListSerializer):
    child = ElementSerializer()
