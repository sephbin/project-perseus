
from django.test import TestCase

from core.models import Element
from web_api.serializers import ElementCudSerializer, ParameterSerializer


class TestParameterSerializer(TestCase):
    def test_create(self):
        element = Element.objects.create(unique_id="1", name="test1")
        data = {
            "guid": "1",
            "name": "test name",
            "value": "test value",
            "value_type": "test value type",
            "element": element.id,
        }
        serializer = ParameterSerializer(data=data)
        serializer.is_valid(raise_exception=True)
        parameter = serializer.save()
        self.assertEqual(parameter.name, "test name")
        self.assertEqual(parameter.value, "test value")
        self.assertEqual(parameter.value_type, "test value type")


class TestElementCudSerializer(TestCase):
    def test_create(self):
        data = {
            "action": "create",
            "unique_id": "1",
            "name": "test1",
            "parameters": [
                {
                    "guid": "1",
                    "name": "test name",
                    "value": "test value",
                    "value_type": "test value type",
                },
            ]
        }
        serializer = ElementCudSerializer()
        element = serializer.act(data)
        self.assertEqual(element.unique_id, "1")
        self.assertEqual(element.name, "test1")
        self.assertEqual(element.parameters.count(), 1)
        parameter = element.parameters.first()
        self.assertEqual(parameter.name, "test name")
        self.assertEqual(parameter.value, "test value")
        self.assertEqual(parameter.value_type, "test value type")


