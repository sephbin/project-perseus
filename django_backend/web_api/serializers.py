from rest_framework import serializers

from core.models import Element, Parameter


class ReadParameterSerializer(serializers.ModelSerializer):
    class Meta:
        model = Parameter
        fields = "__all__"


class ReadElementSerializer(serializers.ModelSerializer):
    parameters = ReadParameterSerializer(many=True)

    class Meta:
        model = Element
        fields = "__all__"


class CreateUpdateParameterSerializer(serializers.Serializer):
    name = serializers.CharField()
    value = serializers.CharField(required=False)
    value_type = serializers.CharField(required=False)


class CreateUpdateElementSerializer(serializers.Serializer):
    unique_id = serializers.CharField()
    name = serializers.CharField(required=False)
    parameters = CreateUpdateParameterSerializer(many=True)

    def validate_parameters(self, parameters):
        if len(parameters) != len(set([parameter['name'] for parameter in parameters])):
            raise serializers.ValidationError('Parameter names must be unique')

        for parameter in parameters:
            p = CreateUpdateParameterSerializer(data=parameter)
            p.is_valid(raise_exception=True)

        return parameters


class CreateElementSerializer(CreateUpdateElementSerializer):

    def save(self):
        parameters = self.validated_data.pop('parameters')
        element = Element.objects.create(**self.validated_data)
        for parameter in parameters:
            Parameter.objects.create(element=element, **parameter)
        return element


class UpdateElementSerializer(CreateUpdateElementSerializer):
    def save(self):
        parameters = self.validated_data.pop('parameters')
        element = Element.objects.get(unique_id=self.validated_data.pop('unique_id'))
        for key, value in self.validated_data.items():
            setattr(element, key, value)
        element.save()
        for parameter in parameters:
            name = parameter.pop('name')
            Parameter.objects.update_or_create(element=element, name=name, defaults=parameter)
        return element


class DeleteElementSerializer(serializers.Serializer):
    unique_id = serializers.CharField()

    def save(self):
        Element.objects.get(unique_id=self.validated_data['unique_id']).delete()


class ElementCudSerializer(serializers.Serializer):
    action = serializers.CharField(write_only=True)  # create update delete
    element = serializers.JSONField(write_only=True)

    def save(self, **kwargs):
        action = self.validated_data['action']
        element = self.validated_data['element']

        serializer = self.get_serializer(action, element)
        serializer.is_valid(raise_exception=True)
        return serializer.save()

    def validate(self, attrs):
        super().validate(attrs)

        action = attrs.get('action')
        element = attrs.get('element')

        self._validate_action(action, element.get('unique_id'))
        self._validate_element(element)

        serializer = self.get_serializer(action, element)
        serializer.is_valid(raise_exception=True)
        attrs['element'] = serializer.validated_data

        return attrs

    def _validate_action(self, action, unique_id):
        if action in ['Update', 'Delete'] and not Element.objects.filter(unique_id=unique_id).exists():
            raise serializers.ValidationError('Element with this unique_id does not exist')
        elif action == 'Create' and Element.objects.filter(unique_id=unique_id).exists():
            raise serializers.ValidationError('Element with this unique_id already exists')

    def _validate_element(self, element):
        if not element:
            raise serializers.ValidationError('Element must be present')

    def get_serializer(self, action, element):
        if action == 'Create':
            return CreateElementSerializer(data=element)
        elif action == 'Update':
            return UpdateElementSerializer(data=element)
        elif action == 'Delete':
            return DeleteElementSerializer(data=element)
        else:
            raise serializers.ValidationError('Invalid action')


class ElementListSerializer(serializers.ListSerializer):
    child = ElementCudSerializer()

    def create(self, validated_data):
        """
        Hack: Despite the name, we also update and delete here.
        """
        return [
            self.child.act(attrs) for attrs in validated_data
        ]
