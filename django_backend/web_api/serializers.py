from rest_framework import serializers

from core.models import *
from rest_framework.utils import model_meta


class ReadParameterSerializer(serializers.ModelSerializer):
    class Meta:
        model = Parameter
        fields = "__all__"


class ReadElementSerializer(serializers.ModelSerializer):
    parameters = serializers.JSONField(source="parameter_dict")
    # parameter_dict = 
    class Meta:
        model = Element
        fields = "__all__"


class CreateUpdateParameterSerializer(serializers.Serializer):
    name = serializers.CharField()
    value = serializers.CharField(required=False)
    value_type = serializers.CharField(required=False)

class CreateUpdateSourceSerializer(serializers.Serializer):
    unique_id = serializers.CharField()
    class Meta:
        model = Source
        fields = ["unique_id",]


class CreateUpdateElementSerializer(serializers.Serializer):

    unique_id = serializers.CharField()
    last_edited_by = serializers.CharField()
    element_id = serializers.CharField()
    name = serializers.CharField(required=False)
    parameters = CreateUpdateParameterSerializer(many=True)
    source_model = CreateUpdateSourceSerializer()
    source_state = serializers.CharField()

    def validate_parameters(self, parameters):
        
        # if len(parameters) != len(set([parameter['name'] for parameter in parameters])):
            # raise serializers.ValidationError('Parameter names must be unique')

        for parameter in parameters:
            p = CreateUpdateParameterSerializer(data=parameter)
            p.is_valid(raise_exception=True)

        return parameters


class CreateElementSerializer(CreateUpdateElementSerializer):

    def save(self):
        # print("CreateElementSerializer")
        parameters = self.validated_data.pop('parameters')

        source_data = self.validated_data.pop('source_model', None)
        source = None
        if source_data:
            # Get or create Source instance
            source, _ = Source.objects.get_or_create(unique_id=source_data['unique_id'])
            self.validated_data['source_model'] = source

        
        element = Element.objects.create(**self.validated_data)
        for parameter in parameters:
            Parameter.objects.create(element=element, **parameter)
        return element


class UpdateElementSerializer(CreateUpdateElementSerializer):
    def save(self):
        # print("CreateUpdateElementSerializer")
        parameters = self.validated_data.pop('parameters')
        
        source_data = self.validated_data.pop('source_model', None)
        source = None
        if source_data:
            source, _ = Source.objects.get_or_create(unique_id=source_data['unique_id'])
            self.validated_data['source_model'] = source


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
    
    def act(self, validated_data):
        action = validated_data.pop('action')

        # print("ElementCudSerializer.act", action)

        if action == 'Create':
            return self.create(validated_data)
        elif action == 'Update':
            # print(validated_data)
            updateElement = Element.objects.get(unique_id=validated_data["element"]["unique_id"])
            return self.update(updateElement, validated_data)
        elif action == 'Delete':
            return self.delete(validated_data)
        else:
            raise Exception(f'Invalid action {action}')

    def create(self, validated_data):
        data = dict(validated_data["element"])
        # print(data)
        # try:
            # del data["parameters"]
        # except: pass
        element = Element.objects.create(**data)
    
    def update(self, instance, validated_data):
        # raise_errors_on_nested_writes('update', self, validated_data)
        info = model_meta.get_field_info(instance)

        # Simply set each attribute on the instance, and then save it.
        # Note that unlike `.create()` we don't need to treat many-to-many
        # relationships as being a special case. During updates we already
        # have an instance pk for the relationships to be associated with.
        m2m_fields = []
        for attr, value in validated_data.items():
            if attr in info.relations and info.relations[attr].to_many:
                m2m_fields.append((attr, value))
            else:
                setattr(instance, attr, value)

        instance.save()

        # Note that many-to-many fields are set after updating instance.
        # Setting m2m fields triggers signals which could potentially change
        # updated instance and we do not want it to collide with .update()
        for attr, value in m2m_fields:
            field = getattr(instance, attr)
            field.set(value)

        return instance

    def save(self, validated_data, **kwargs):
        action = validated_data['action']
        element = validated_data['element']
        # print("about to get_serializer")
        serializer = self.get_serializer(action, element)
        serializer.is_valid(raise_exception=True)
        # print(serializer)
        # print("about to save")
        return serializer.save()

    def validate(self, attrs):
        # print(" "*5+"in ElementCudSerializer.validate:")
        super().validate(attrs)

        action = attrs.get('action')
        element = attrs.get('element')

        self._validate_action(action, element.get('unique_id'))
        self._validate_element(element)
        # print(" "*10+"about to get_serializer")
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
        # print(" "*5+"ElementCudSerializer.get_serializer")
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
            self.child.save(attrs) for attrs in validated_data
        ]
