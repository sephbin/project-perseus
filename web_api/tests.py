from django.core.management import call_command
from django.test import TestCase

from core.models import Element
from web_api.serializers import ElementCudSerializer, CreateUpdateParameterSerializer


class TestSerializers(TestCase):
    def test_parameter_serializer(self):
        data = {
            "guid": "1",
            "name": "test name",
            "value": "test value",
            "value_type": "test value type",
        }
        serializer = CreateUpdateParameterSerializer(data=data)
        serializer.is_valid(raise_exception=True)

    def test_create_element_serializer(self):
        data = {
            "action": "Create",
            "element": {
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
        }
        serializer = ElementCudSerializer(data=data)
        serializer.is_valid(raise_exception=True)
        element = serializer.save()
        self.assertEqual(element.unique_id, "1")
        self.assertEqual(element.name, "test1")
        self.assertEqual(element.parameters.count(), 1)
        parameter = element.parameters.first()
        self.assertEqual(parameter.name, "test name")
        self.assertEqual(parameter.value, "test value")
        self.assertEqual(parameter.value_type, "test value type")

    def test_update_element_serializer(self):
        input_element = Element.objects.create(unique_id="1", name="test1")
        input_parameter = input_element.parameters.create(name="test name", value="test value", value_type="test value type")
        data = {
            "action": "Update",
            "element": {
                "unique_id": input_element.unique_id,
                "name": input_element.name + " modified",
                "parameters": [
                    {
                        "name": input_parameter.name,
                        "value": input_parameter.value + " modified",
                        "value_type": input_parameter.value_type,
                    },
                ]
            }
        }
        serializer = ElementCudSerializer(data=data)
        serializer.is_valid(raise_exception=True)
        output_element = serializer.save()
        self.assertEqual(output_element.unique_id, "1")
        self.assertEqual(output_element.name, "test1 modified")
        self.assertEqual(output_element.parameters.count(), 1)
        input_parameter = input_element.parameters.first()
        self.assertEqual(input_parameter.name, "test name")
        self.assertEqual(input_parameter.value, "test value modified")
        self.assertEqual(input_parameter.value_type, "test value type")


    def test_delete_element_serializer(self):
        input_element = Element.objects.create(unique_id="1", name="test1")
        data = {
            "action": "Delete",
            "element": {
                "unique_id": input_element.unique_id,
            }
        }
        serializer = ElementCudSerializer(data=data)
        serializer.is_valid(raise_exception=True)
        serializer.save()
        self.assertFalse(Element.objects.filter(unique_id="1").exists())

class TestViews(TestCase):
    def test_element_delta_submission_view(self):
        # call the django management seed command
        call_command("seed")
        self.client.login(username='admin', password='x')

        data = [
            {
                "action": "Create",
                "element": {
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
            },
            {
                "action": "Update",
                "element": {
                    "unique_id": "1",
                    "name": "test1 modified",
                    "parameters": [
                        {
                            "name": "test name",
                            "value": "test value modified",
                            "value_type": "test value type",
                        },
                    ]
                }
            },
            {
                "action": "Delete",
                "element": {
                    "unique_id": "1",
                }
            },
        ]
        response = self.client.post('/rapi/elements/', data=data, content_type='application/json')
        self.assertEqual(response.status_code, 201)
        self.assertFalse(Element.objects.filter(unique_id="1").exists())
