from rest_framework import viewsets
from rest_framework.response import Response
from rest_framework.viewsets import ReadOnlyModelViewSet
from django_filters.rest_framework import DjangoFilterBackend
from core.models import *
from web_api.serializers import ElementListSerializer, ReadElementSerializer, ReadParameterSerializer
from web_api.filters import *
import json
from django.views.decorators.csrf import csrf_exempt
from django.http import HttpResponse, JsonResponse

class ElementDeltaSubmissionView(ReadOnlyModelViewSet):
    queryset = Element.objects.all()
    serializer_class = ReadElementSerializer
    create_serializer_class = ElementListSerializer
    
    filter_backends = [DjangoFilterBackend]
    # filterset_class = ElementModelFilter
    filterset_fields = {
        'name': ['exact'],
        'element_id': ['exact'],
        'created_at': ['exact', 'gte', 'lte', 'gt', 'lt', 'range'],
        'updated_at': ['exact', 'gte', 'lte', 'gt', 'lt', 'range'],
        'parameters__name':['exact'],
        'parameters__value':['exact'],
        'source_model':['exact'],
        'source_model__name':['exact'],
        'source_model__unique_id':['exact'],
    }

    def create(self, request, *args, **kwargs):
        # print("ElementDeltaSubmissionView.Create")
        

        """
        This method is called create to hook into the DRF viewset lifecycle.
        """
        data = request.data if isinstance(request.data, list) else [request.data]
        
        for index, i in enumerate(data):
            # i["action"] = "Create"
            # if i['action'] == "Create":
                # i['action'] = "Update"
            
            

            if i["element"]["name"] == "": i["element"]["name"] = "<none>"
            cleanParams = []
            for param in i["element"]["parameters"]:
                if param == {}: continue
                if "value" not in param:
                    param["value"]="<none>"
                if param["value"] == None:
                    param["value"]="<none>"
                if type(param["value"]) == type(""):
                    if param["value"].strip() == "":
                        param["value"]="<blank>"
                if param in cleanParams: continue
                cleanParams.append(param)
            i["element"]["parameters"] = cleanParams

            # sourceModel, created = Source.objects.get_or_create(unique_id=i["element"]["source_model"])
            i["element"]["source_model"] = {"unique_id":i["element"]["source_model"]}
            # if index == 0: print(i)
            #print(str(i["element"]["element_id"])+ json.dumps(i["element"]["parameters"]))
            # print(i["element"])
        print(list(map(lambda x: x["action"]+": "+str(x["element"]["element_id"])+" | "+x["element"]["unique_id"], data)))
        # data = [data[0]]
        # print(json.dumps(data, indent=4))
        # data = data[4090:]
        # print(data[0])
        serializer = self.create_serializer_class(data=data)

        try:
            serializer.is_valid(raise_exception=True)
            # print("is_valid")
            serializer.save(data=data)
            # headers = self.get_success_headers(data)
            return Response(serializer.data, status=201)
        except Exception as e:
            print(e)
            return Response({}, status=404)


    def get_queryset(self):
            queryset = super().get_queryset()
            ids = self.request.query_params.get('ids')
            if ids:
                id_list = [int(i) for i in ids.split(',') if i.isdigit()]
                queryset = queryset.filter(id__in=id_list)
            return queryset


class ParameterReadView(ReadOnlyModelViewSet):
    queryset = Parameter.objects.all()
    serializer_class = ReadParameterSerializer
    
    filter_backends = [DjangoFilterBackend]
    filterset_fields = {
        'name': ['exact'],
        'value': ['exact'],
    }


@csrf_exempt
def stateUpdate(request):
    import json
    import copy
    data = []
    if request.method == "POST":
        data = json.loads(request.body)

    createElements = []
    createParams = []
    for index, line in enumerate(copy.deepcopy(data)):
        element = line["element"]
        unique_id = element.pop("unique_id")
        parameters = element.pop("parameters")
        last_edited_by = element.pop("last_edited_by")
        
        sourceModel = element.pop("source_model")
        sourceModel, _created = Source.objects.get_or_create(unique_id=sourceModel)
        element["source_model"] = sourceModel

        if index == 0: print(line)
        # modelElement, _updated = Element.objects.update_or_create(unique_id = unique_id, defaults=element)
        appendElement = Element(unique_id=unique_id ,**element)
        createElements.append(appendElement)

        
        # existingParamNames = list(set(map(lambda x: x.name, modelElement.parameters.all())))
        ## Preparing to remove deleted parameters
        # print(existingParamNames)
        

    Element.objects.bulk_create(
            createElements,
            update_conflicts=True,
            unique_fields=['unique_id'],
            update_fields=['element_id', 'name', 'source_model_id', 'source_state'])
    
    for index, line in enumerate(data):
        element = line["element"]
        unique_id = element.pop("unique_id")
        elementModel = Element.objects.get(unique_id=unique_id)
        for param in parameters:
            try:
                print(param)
                name = param.pop("name")
                appendParam = Parameter(element=elementModel, name=name, **param)
                createParams.append(appendParam)
            except Exception as e:
                print(e)
                pass
    try:
        # print(createParams)
        Parameter.objects.bulk_create(
        createParams,
        update_conflicts=True,
        unique_fields=['element','name'],
        update_fields=['value','value_type'])
    except Exception as e:
        print(e)

    # Return the custom 202 Accepted response
    return HttpResponse()

def getLatestState(request, source):
        sourceModel, _created = Source.objects.get_or_create(unique_id=source)
        element = Element.objects.filter(source_model=sourceModel).order_by('-updated_at').first()
        return  JsonResponse({"value":element.source_state})