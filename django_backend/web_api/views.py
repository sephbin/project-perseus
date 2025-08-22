from rest_framework import viewsets
from rest_framework.response import Response
from rest_framework.viewsets import ReadOnlyModelViewSet

from core.models import Element
from web_api.serializers import ElementListSerializer, ReadElementSerializer


class ElementDeltaSubmissionView(ReadOnlyModelViewSet):
    queryset = Element.objects.all()
    serializer_class = ReadElementSerializer
    create_serializer_class = ElementListSerializer

    def create(self, request, *args, **kwargs):
        """
        This method is called create to hook into the DRF viewset lifecycle.
        """
        data = request.data if isinstance(request.data, list) else [request.data]

        serializer = self.create_serializer_class(request.data)
        serializer.is_valid(raise_exception=True)
        serializer.save(request.data)

        headers = self.get_success_headers(data)
        return Response(serializer.data, status=201, headers=headers)
