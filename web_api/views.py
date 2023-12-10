
from rest_framework import generics, viewsets
from rest_framework.response import Response

from core.models import Element
from web_api.serializers import ElementSerializer, ElementListSerializer


class ElementViewSet(viewsets.ModelViewSet):
    queryset = Element.objects.all()
    serializer_class = ElementSerializer
    create_or_update_serializer_class = ElementListSerializer

    def create(self, request, *args, **kwargs):
        data = request.data if isinstance(request.data, list) else [request.data]

        serializer = self.create_or_update_serializer_class(data=data)
        serializer.is_valid(raise_exception=True)
        self.perform_create(serializer)

        headers = self.get_success_headers(serializer.data)
        return Response(serializer.data, status=201, headers=headers)

    # def update(self, request, *args, **kwargs):
    #     data = request.data if isinstance(request.data, list) else [request.data]
    #
    #     serializer = self.create_or_update_serializer_class(data=data)
    #     serializer.is_valid(raise_exception=True)
    #     self.perform_update(serializer)
    #
    #     headers = self.get_success_headers(serializer.data)
    #     return Response(serializer.data, status=200, headers=headers)


