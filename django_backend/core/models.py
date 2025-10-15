from django.db import models


class Source(models.Model):
    unique_id = models.TextField(unique=True)
    name = models.TextField(blank=True)
    medium = models.TextField(blank=True)


class Element(models.Model):
    element_id = models.TextField()
    unique_id = models.TextField(unique=True)
    name = models.TextField(blank=True)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    last_edited_by = models.TextField(blank=True)
    source_model = models.ForeignKey('Source', on_delete=models.CASCADE, related_name='elements', blank=True, null=True)
    source_state = models.TextField(blank=True, null=True)

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
    name = models.TextField(blank=True, null=True)
    value = models.TextField(blank=True, null=True)
    value_type = models.TextField(blank=True, null=True)
    element = models.ForeignKey('Element', on_delete=models.CASCADE, related_name='parameters')

    # class Meta:
        # unique_together = ('element', 'name')