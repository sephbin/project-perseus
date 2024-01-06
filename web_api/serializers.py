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


class ElementCudSerializer(ElementSerializer):
    action = serializers.CharField(write_only=True)  # create update delete

    def act(self, validated_data):
        action = validated_data.pop('action')

        if action == 'Create':
            return self.create(validated_data)
        elif action == 'Update':
            return self.update(validated_data)
        elif action == 'Delete':
            return self.delete(validated_data)
        else:
            raise Exception(f'Invalid action {action}')

    def create(self, validated_data):
        parameters = validated_data.pop('parameters')
        element = Element.objects.create(**validated_data)
        for parameter in parameters:
            Parameter.objects.create(element=element, **parameter)
        return element

    def update(self, validated_data):
        parameters = validated_data.pop('parameters')
        element = Element.objects.get(pk=validated_data.pop('id'))
        for parameter in parameters:
            Parameter.objects.update_or_create(element=element, **parameter)
        return element

    def delete(self, validated_data):
        element = Element.objects.get(pk=validated_data.pop('id'))
        element.delete()
        return element


class ElementListSerializer(serializers.ListSerializer):
    child = ElementCudSerializer()

    def create(self, validated_data):
        """
        Hack: Despite the name, we also update and delete here.
        """
        return [
            self.child.act(attrs) for attrs in validated_data
        ]
