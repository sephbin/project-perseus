from django.db import models


class Source(models.Model):
    unique_id = models.CharField(max_length=128, unique=True)
    name = models.TextField(max_length=255, blank=True)
    medium = models.TextField(max_length=255, blank=True)


class Element(models.Model):
    element_id = models.TextField(max_length=255)
    unique_id = models.CharField(max_length=128, unique=True)
    name = models.TextField(max_length=255, blank=True)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    last_edited_by = models.TextField(max_length=255, blank=True)
    source_model = models.ForeignKey('Source', on_delete=models.CASCADE, related_name='elements', blank=True, null=True)
    source_state = models.TextField(max_length=255, blank=True, null=True)

    @property
    def parameterList(self):
        params = list(map(lambda x: x.name+": "+x.value, self.parameters.all()))
        params = "\n".join(params)
        return params
    @property 
    def parameter_dict(self):
        outDict = {} 
        for param in self.parameters.all():
            value = param.value
            # print(param.value_type, param.value)
            if param.value_type == "ElementId":
                try: value = Element.objects.get(element_id=value).name
                except Exception as e:
                    # print(e)
                    pass
            outDict[param.name] = value
        return outDict

    


class Parameter(models.Model):
    name = models.CharField(max_length=255, blank=True, null=True)
    value = models.TextField(max_length=255, blank=True, null=True)
    value_type = models.TextField(max_length=255, blank=True, null=True)
    element = models.ForeignKey('Element', on_delete=models.CASCADE, related_name='parameters')

    class Meta:
        constraints = [
            models.UniqueConstraint(fields=['element', 'name'], name='unique_element_parameter_name')
        ]