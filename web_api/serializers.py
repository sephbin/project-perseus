from rest_framework import serializers

from core.models import Element, Parameter


class ParameterSerializer(serializers.ModelSerializer):
    class Meta:
        model = Parameter
        fields = '__all__'
        read_only_fields = ['id', 'element']


class ElementSerializer(serializers.ModelSerializer):
    parameters = ParameterSerializer(many=True)

    class Meta:
        model = Element
        fields = '__all__'
        read_only_fields = ['id']

    def create(self, validated_data):
        parameters_data = validated_data.pop('parameters')
        element = Element.objects.create(**validated_data)
        for parameter_data in parameters_data:
            Parameter.objects.create(element=element, **parameter_data)
        return element


class ElementListSerializer(serializers.ListSerializer):
    child = ElementSerializer()
